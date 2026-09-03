using System.Text;
using System.Text.RegularExpressions;
using StackPivot.Agent.Security;

namespace StackPivot.Agent.Execution;

internal sealed class ComposeWorkspaceSnapshot : IDisposable
{
    private const int MaxSnapshotFileCount = 4096;
    private const int MaxSnapshotDirectoryCount = 4096;
    private const int MaxSnapshotEntryCount = 8192;
    private const long MaxSnapshotTotalBytes = 64L * 1024 * 1024;
    private const long MaxSnapshotFileBytes = 16L * 1024 * 1024;
    private const int MaxSnapshotPathDepth = 32;
    private const int MaxComposeReferenceDepth = 8;
    private const int MaxComposeDocuments = 64;
    private const long MaxComposeDocumentBytes = 1L * 1024 * 1024;
    private const long MaxComposeInputBytes = 4L * 1024 * 1024;

    private static readonly HashSet<string> PathKeys = new(StringComparer.Ordinal)
    {
        "build",
        "cache_from",
        "cache_to",
        "env_file",
        "file",
        "context",
        "device",
        "dest",
        "dockerfile",
        "additional_contexts",
        "include",
        "path",
        "src",
        "project_directory",
        "ssh",
        "source",
        "volumes",
        "devices"
    };

    private readonly TemporaryDirectory temporaryDirectory;
    private bool disposed;

    private ComposeWorkspaceSnapshot(TemporaryDirectory temporaryDirectory, string composeFileName)
    {
        this.temporaryDirectory = temporaryDirectory;
        WorkingDirectory = temporaryDirectory.FullPath;
        WorkingDirectoryHandle = temporaryDirectory.Directory;
        ComposeFileName = composeFileName;
    }

    public string WorkingDirectory { get; }

    public SafeDirectoryHandle WorkingDirectoryHandle { get; }

    public string ComposeFileName { get; }

    public static ComposeWorkspaceSnapshot Create(
        SafeDirectoryHandle source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        var temporaryDirectory = SafeDirectoryHandle.CreateTemporaryDirectory("compose-");
        try
        {
            CopyDirectory(
                source,
                temporaryDirectory.Directory,
                isRoot: true,
                depth: 0,
                new SnapshotBudget(),
                cancellationToken);
            var composeFileName = ValidateComposeReferences(temporaryDirectory.Directory);
            return new ComposeWorkspaceSnapshot(temporaryDirectory, composeFileName);
        }
        catch
        {
            temporaryDirectory.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        temporaryDirectory.Dispose();
    }

    private static void CopyDirectory(
        SafeDirectoryHandle source,
        SafeDirectoryHandle destination,
        bool isRoot,
        int depth,
        SnapshotBudget budget,
        CancellationToken cancellationToken,
        bool directoryAlreadyCounted = false)
    {
        if (depth > MaxSnapshotPathDepth)
        {
            throw new PathPolicyException("Compose workspace path depth exceeds the safety limit.");
        }

        if (!directoryAlreadyCounted)
        {
            budget.StartDirectory();
        }

        foreach (var name in source.EnumerateEntryNames(MaxSnapshotFileCount + 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.StartEntry();
            if (name is "." or "..")
            {
                throw new PathPolicyException("Directory enumeration returned an unsafe entry.");
            }

            if (isRoot && name == ".git")
            {
                continue;
            }

            if (name == ".git" || IsSensitiveEnvironmentFile(name))
            {
                throw new PathPolicyException("Compose workspace contains a prohibited file.");
            }

            var sourceDirectory = source.TryOpenChildDirectory(name);
            if (sourceDirectory is not null)
            {
                if (depth == MaxSnapshotPathDepth)
                {
                    throw new PathPolicyException("Compose workspace path depth exceeds the safety limit.");
                }

                budget.StartDirectory();
                using (sourceDirectory)
                using (var destinationDirectory = destination.OpenChildDirectory(name, create: true))
                {
                    CopyDirectory(
                        sourceDirectory,
                        destinationDirectory,
                        isRoot: false,
                        depth + 1,
                        budget,
                        cancellationToken,
                        directoryAlreadyCounted: true);
                }

                continue;
            }

            using var sourceFile = source.OpenRegularFile(name);
            budget.StartFile(sourceFile.Length);
            using var destinationFile = destination.OpenFile(name, FileMode.Create, FileAccess.Write);
            var buffer = budget.Buffer;
            long copiedBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = sourceFile.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                copiedBytes = checked(copiedBytes + read);
                if (copiedBytes > MaxSnapshotFileBytes)
                {
                    throw new PathPolicyException("Compose workspace contains an oversized file.");
                }

                budget.ConsumeBytes(read);
                destinationFile.Write(buffer, 0, read);
            }
        }
    }

    private static string ValidateComposeReferences(SafeDirectoryHandle workspace)
    {
        var state = new ComposeReferenceState();
        var rootFileName = FindComposeFileName(workspace);
        ValidateComposeDocument(workspace, rootFileName, depth: 0, state);
        return rootFileName;
    }

    private static void ValidateComposeDocument(
        SafeDirectoryHandle workspace,
        string relativeFileName,
        int depth,
        ComposeReferenceState state)
    {
        using var file = OpenReferencedComposeFile(workspace, relativeFileName);
        var length = file.Length;
        state.Enter(relativeFileName, depth, length);
        try
        {
            string compose;
            try
            {
                using var reader = new StreamReader(
                    file,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
                compose = reader.ReadToEnd();
            }
            catch (DecoderFallbackException exception)
            {
                throw new PathPolicyException("Compose file is not valid UTF-8.", exception);
            }

            if (Encoding.UTF8.GetByteCount(compose) > MaxComposeDocumentBytes)
            {
                throw new PathPolicyException("Compose document exceeds the input size limit.");
            }

            var document = new ComposeYamlParser(compose).Parse();
            ValidateComposeNode(document, ComposeValidationContext.Root);
            VisitComposeReferences(workspace, relativeFileName, document, depth, state);
        }
        finally
        {
            state.Exit(relativeFileName);
        }
    }

    private static void VisitComposeReferences(
        SafeDirectoryHandle workspace,
        string currentFileName,
        ComposeNode node,
        int depth,
        ComposeReferenceState state)
    {
        var root = RequireMap(node, "Compose document");
        foreach (var property in root.Properties)
        {
            if (property.Key == "include")
            {
                VisitIncludeReferences(workspace, currentFileName, property.Value, depth, state);
            }
            else if (property.Key == "services")
            {
                VisitServiceReferences(workspace, currentFileName, property.Value, depth, state);
            }
        }
    }

    private static void VisitServiceReferences(
        SafeDirectoryHandle workspace,
        string currentFileName,
        ComposeNode node,
        int depth,
        ComposeReferenceState state)
    {
        var services = RequireMap(node, "Compose services");
        foreach (var service in services.Properties)
        {
            var serviceMap = RequireMap(service.Value, "Compose service");
            foreach (var property in serviceMap.Properties)
            {
                if (property.Key == "extends")
                {
                    VisitExtendsReference(workspace, currentFileName, property.Value, depth, state);
                }
                else if (property.Key == "include")
                {
                    VisitIncludeReferences(workspace, currentFileName, property.Value, depth, state);
                }
            }
        }
    }

    private static void VisitExtendsReference(
        SafeDirectoryHandle workspace,
        string currentFileName,
        ComposeNode node,
        int depth,
        ComposeReferenceState state)
    {
        var map = RequireMap(node, "Compose extends");
        foreach (var property in map.Properties)
        {
            if (property.Key == "file")
            {
                var path = RequireReferenceScalar(property.Value, "extends.file");
                var referencedFileName = ResolveReferencePath(currentFileName, path.Value, "extends.file");
                ValidateComposeDocument(workspace, referencedFileName, depth + 1, state);
            }
        }
    }

    private static void VisitIncludeReferences(
        SafeDirectoryHandle workspace,
        string currentFileName,
        ComposeNode node,
        int depth,
        ComposeReferenceState state)
    {
        switch (node)
        {
            case ComposeScalar scalar:
                VisitIncludedFile(workspace, currentFileName, scalar, depth, state);
                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    VisitIncludeItem(workspace, currentFileName, item, depth, state);
                }

                break;
            case ComposeMap map:
                VisitIncludeItem(workspace, currentFileName, map, depth, state);
                break;
            default:
                throw new PathPolicyException("Compose include has an unsupported value.");
        }
    }

    private static void VisitIncludeItem(
        SafeDirectoryHandle workspace,
        string currentFileName,
        ComposeNode node,
        int depth,
        ComposeReferenceState state)
    {
        if (node is ComposeScalar scalar)
        {
            VisitIncludedFile(workspace, currentFileName, scalar, depth, state);
            return;
        }

        var map = RequireMap(node, "Compose include entry");
        foreach (var property in map.Properties)
        {
            if (property.Key != "path")
            {
                continue;
            }

            switch (property.Value)
            {
                case ComposeScalar scalarValue:
                    VisitIncludedFile(workspace, currentFileName, scalarValue, depth, state);
                    break;
                case ComposeSequence sequence:
                    foreach (var item in sequence.Items)
                    {
                        VisitIncludedFile(
                            workspace,
                            currentFileName,
                            RequireReferenceScalar(item, "include.path"),
                            depth,
                            state);
                    }

                    break;
                default:
                    throw new PathPolicyException("Compose include.path must be a scalar or sequence.");
            }
        }
    }

    private static void VisitIncludedFile(
        SafeDirectoryHandle workspace,
        string currentFileName,
        ComposeScalar scalar,
        int depth,
        ComposeReferenceState state)
    {
        var referencedFileName = ResolveReferencePath(currentFileName, scalar.Value, "include.path");
        ValidateComposeDocument(workspace, referencedFileName, depth + 1, state);
    }

    private static ComposeScalar RequireReferenceScalar(ComposeNode node, string propertyName)
    {
        var scalar = RequireScalar(node, propertyName);
        if (scalar.IsBlockScalar || scalar.IsNull || scalar.Value.Length == 0)
        {
            throw new PathPolicyException("Compose reference paths must be non-empty scalar values.");
        }

        return scalar;
    }

    private static string ResolveReferencePath(string currentFileName, string value, string propertyName)
    {
        ValidateReferencePathValue(value, propertyName);
        var currentDirectory = Path.GetDirectoryName(currentFileName)?.Replace('\\', '/') ?? string.Empty;
        var combined = string.IsNullOrEmpty(currentDirectory)
            ? value
            : currentDirectory + "/" + value;
        var segments = combined.Split('/', StringSplitOptions.None);
        if (segments.Length > MaxSnapshotPathDepth
            || segments.Any(segment => segment.Length == 0 || segment is ".." or ".git"))
        {
            throw new PathPolicyException("Compose reference path exceeds the private workspace boundary.");
        }

        var normalizedSegments = segments.Where(segment => segment != ".").ToArray();
        if (normalizedSegments.Length == 0)
        {
            throw new PathPolicyException("Compose reference path must name a file.");
        }

        return string.Join('/', normalizedSegments);
    }

    private static void ValidateReferencePathValue(string value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || value.Contains('$')
            || value.Contains('\\')
            || value.Contains(':')
            || value.Contains('*')
            || value.StartsWith('~')
            || Path.IsPathFullyQualified(value))
        {
            throw new PathPolicyException("Compose " + propertyName + " is not a safe relative path.");
        }

        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Length == 0
            || segments.Any(segment => segment.Length == 0 || segment is ".." or ".git"))
        {
            throw new PathPolicyException("Compose " + propertyName + " is not a safe relative path.");
        }
    }

    private static FileStream OpenReferencedComposeFile(
        SafeDirectoryHandle workspace,
        string relativeFileName)
    {
        var segments = relativeFileName.Split('/', StringSplitOptions.None);
        SafeDirectoryHandle current = workspace;
        SafeDirectoryHandle? ownedDirectory = null;
        try
        {
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var next = current.TryOpenChildDirectory(segments[index])
                    ?? throw new PathPolicyException("Compose reference file is missing or is not a directory.");
                ownedDirectory?.Dispose();
                ownedDirectory = next;
                current = next;
            }

            return current.OpenRegularFile(segments[^1]);
        }
        finally
        {
            ownedDirectory?.Dispose();
        }
    }

    private sealed class SnapshotBudget
    {
        public byte[] Buffer { get; } = new byte[64 * 1024];

        private int fileCount;
        private int directoryCount;
        private int entryCount;
        private long totalBytes;

        public void StartDirectory()
        {
            directoryCount++;
            if (directoryCount > MaxSnapshotDirectoryCount)
            {
                throw new PathPolicyException("Compose workspace contains too many directories.");
            }
        }

        public void StartEntry()
        {
            entryCount++;
            if (entryCount > MaxSnapshotEntryCount)
            {
                throw new PathPolicyException("Compose workspace contains too many entries.");
            }
        }

        public void StartFile(long expectedLength)
        {
            if (expectedLength < 0 || expectedLength > MaxSnapshotFileBytes)
            {
                throw new PathPolicyException("Compose workspace contains an oversized file.");
            }

            fileCount++;
            if (fileCount > MaxSnapshotFileCount)
            {
                throw new PathPolicyException("Compose workspace contains too many files.");
            }
        }

        public void ConsumeBytes(int count)
        {
            totalBytes = checked(totalBytes + count);
            if (totalBytes > MaxSnapshotTotalBytes)
            {
                throw new PathPolicyException("Compose workspace exceeds the total input size limit.");
            }
        }
    }

    private sealed class ComposeReferenceState
    {
        private readonly HashSet<string> active = new(StringComparer.Ordinal);
        private readonly HashSet<string> visited = new(StringComparer.Ordinal);
        private long inputBytes;
        private int documentCount;

        public void Enter(string fileName, int depth, long length)
        {
            if (depth > MaxComposeReferenceDepth)
            {
                throw new PathPolicyException("Compose reference depth exceeds the safety limit.");
            }

            if (!active.Add(fileName))
            {
                throw new PathPolicyException("Compose references contain a cycle.");
            }

            if (!visited.Add(fileName))
            {
                active.Remove(fileName);
                throw new PathPolicyException("Compose references contain a duplicate file.");
            }

            documentCount++;
            if (documentCount > MaxComposeDocuments)
            {
                active.Remove(fileName);
                throw new PathPolicyException("Compose references contain too many documents.");
            }

            if (length < 0 || length > MaxComposeDocumentBytes || inputBytes > MaxComposeInputBytes - length)
            {
                active.Remove(fileName);
                throw new PathPolicyException("Compose references exceed the input size limit.");
            }

            inputBytes += length;
        }

        public void Exit(string fileName)
        {
            active.Remove(fileName);
        }
    }

    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal)
    {
        "version",
        "name",
        "services",
        "networks",
        "volumes",
        "configs",
        "secrets",
        "include",
        "models",
        "fragments",
        "merge"
    };

    private static readonly HashSet<string> ServiceKeys = new(StringComparer.Ordinal)
    {
        "annotations",
        "attach",
        "build",
        "cap_add",
        "cap_drop",
        "cgroup",
        "cgroup_parent",
        "command",
        "configs",
        "container_name",
        "cpu_count",
        "cpu_percent",
        "cpu_period",
        "cpu_quota",
        "cpu_rt_period",
        "cpu_rt_runtime",
        "cpus",
        "cpuset",
        "cpu_shares",
        "credential_spec",
        "depends_on",
        "deploy",
        "description",
        "devices",
        "develop",
        "dns",
        "dns_opt",
        "dns_search",
        "domainname",
        "entrypoint",
        "env_file",
        "environment",
        "expose",
        "extends",
        "external_links",
        "extra_hosts",
        "gpus",
        "group_add",
        "healthcheck",
        "hostname",
        "image",
        "init",
        "ipc",
        "isolation",
        "labels",
        "links",
        "logging",
        "mac_address",
        "mem_limit",
        "mem_reservation",
        "mem_swappiness",
        "memswap_limit",
        "models",
        "networks",
        "network_mode",
        "oom_kill_disable",
        "oom_score_adj",
        "pid",
        "pids_limit",
        "platform",
        "ports",
        "privileged",
        "profiles",
        "pull_policy",
        "read_only",
        "restart",
        "runtime",
        "scale",
        "secrets",
        "security_opt",
        "shm_size",
        "stdin_open",
        "stop_grace_period",
        "stop_signal",
        "storage_opt",
        "sysctls",
        "tmpfs",
        "tty",
        "ulimits",
        "user",
        "userns_mode",
        "uts",
        "volumes",
        "volumes_from",
        "working_dir",
        "post_start",
        "pre_stop",
        "provider",
        "interface_name"
    };

    private static readonly HashSet<string> BuildKeys = new(StringComparer.Ordinal)
    {
        "context",
        "dockerfile",
        "dockerfile_inline",
        "args",
        "ssh",
        "cache_from",
        "cache_to",
        "additional_contexts",
        "target",
        "network",
        "extra_hosts",
        "isolation",
        "labels",
        "secrets",
        "platforms",
        "tags",
        "privileged",
        "entitlements",
        "provenance",
        "sbom",
        "no_cache",
        "pull",
        "shm_size",
        "ulimits",
        "outputs"
    };

    private static readonly HashSet<string> ExtendsKeys = new(StringComparer.Ordinal)
    {
        "file",
        "service"
    };

    private static readonly HashSet<string> EnvFileKeys = new(StringComparer.Ordinal)
    {
        "path",
        "required",
        "format"
    };

    private static readonly HashSet<string> IncludeKeys = new(StringComparer.Ordinal)
    {
        "path",
        "env_file",
        "project_directory"
    };

    private static readonly HashSet<string> MountKeys = new(StringComparer.Ordinal)
    {
        "type",
        "source",
        "target",
        "read_only",
        "consistency",
        "bind",
        "volume",
        "tmpfs",
        "image",
        "subpath",
        "nocopy",
        "create_host_path",
        "selinux",
        "propagation",
        "recursive",
        "device"
    };

    private static readonly HashSet<string> BindMountKeys = new(StringComparer.Ordinal)
    {
        "propagation",
        "create_host_path",
        "selinux",
        "recursive"
    };

    private static readonly HashSet<string> VolumeMountKeys = new(StringComparer.Ordinal)
    {
        "nocopy",
        "subpath"
    };

    private static readonly HashSet<string> TmpfsMountKeys = new(StringComparer.Ordinal)
    {
        "size",
        "mode"
    };

    private static readonly HashSet<string> ImageMountKeys = new(StringComparer.Ordinal)
    {
        "subpath"
    };

    private static readonly HashSet<string> ResourceDefinitionKeys = new(StringComparer.Ordinal)
    {
        "name",
        "external",
        "file",
        "environment",
        "content",
        "template_driver",
        "driver",
        "driver_opts",
        "labels",
        "image",
        "uid",
        "gid",
        "mode"
    };

    private static readonly HashSet<string> ServiceResourceKeys = new(StringComparer.Ordinal)
    {
        "source",
        "target",
        "uid",
        "gid",
        "mode"
    };

    private static readonly HashSet<string> CacheKeys = new(StringComparer.Ordinal)
    {
        "type",
        "ref",
        "src",
        "dest",
        "mode",
        "compression",
        "oci-mediatypes",
        "ignore-error",
        "sharing",
        "scope",
        "image-manifest",
        "registry.insecure",
        "inline",
        "platform"
    };

    private static readonly HashSet<string> DevelopKeys = new(StringComparer.Ordinal)
    {
        "watch"
    };

    private static readonly HashSet<string> WatchKeys = new(StringComparer.Ordinal)
    {
        "include",
        "path",
        "target",
        "action",
        "ignore",
        "initial_sync",
        "exec"
    };

    private static readonly Regex ServiceName = new(
        "^[A-Za-z0-9][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private enum ComposeValidationContext
    {
        Root,
        Services,
        Service,
        Generic,
        Extends,
        Build,
        EnvFile,
        EnvFileItem,
        Include,
        IncludeItem,
        MountCollection,
        MountItem,
        MountOptions,
        TopLevelResources,
        ResourceDefinition,
        ServiceResources,
        ServiceResourceItem,
        AdditionalContexts,
        Cache,
        CacheItem,
        Ssh,
        Develop,
        Watch
    }

    private static void ValidateComposeNode(ComposeNode node, ComposeValidationContext context)
    {
        switch (context)
        {
            case ComposeValidationContext.Root:
                ValidateRoot(node);
                break;
            case ComposeValidationContext.Services:
                ValidateServices(node);
                break;
            case ComposeValidationContext.Service:
                ValidateService(node);
                break;
            case ComposeValidationContext.Generic:
                ValidateGeneric(node);
                break;
            case ComposeValidationContext.Extends:
                ValidateExtends(node);
                break;
            case ComposeValidationContext.Build:
                ValidateBuild(node);
                break;
            case ComposeValidationContext.EnvFile:
                ValidateEnvFile(node);
                break;
            case ComposeValidationContext.EnvFileItem:
                ValidateEnvFileItem(node);
                break;
            case ComposeValidationContext.Include:
                ValidateInclude(node);
                break;
            case ComposeValidationContext.IncludeItem:
                ValidateIncludeItem(node);
                break;
            case ComposeValidationContext.MountCollection:
                ValidateMountCollection(node);
                break;
            case ComposeValidationContext.MountItem:
                ValidateMountItem(node);
                break;
            case ComposeValidationContext.MountOptions:
                ValidateMountOptions(node);
                break;
            case ComposeValidationContext.TopLevelResources:
                ValidateTopLevelResources(node);
                break;
            case ComposeValidationContext.ResourceDefinition:
                ValidateResourceDefinition(node);
                break;
            case ComposeValidationContext.ServiceResources:
                ValidateServiceResources(node);
                break;
            case ComposeValidationContext.ServiceResourceItem:
                ValidateServiceResourceItem(node);
                break;
            case ComposeValidationContext.AdditionalContexts:
                ValidateAdditionalContexts(node);
                break;
            case ComposeValidationContext.Cache:
                ValidateCache(node);
                break;
            case ComposeValidationContext.CacheItem:
                ValidateCacheItem(node);
                break;
            case ComposeValidationContext.Ssh:
                ValidateSsh(node);
                break;
            case ComposeValidationContext.Develop:
                ValidateDevelop(node);
                break;
            case ComposeValidationContext.Watch:
                ValidateWatch(node);
                break;
            default:
                throw new PathPolicyException("Compose file uses an unsupported validation context.");
        }
    }

    private static void ValidateRoot(ComposeNode node)
    {
        var map = RequireMap(node, "Compose document");
        foreach (var property in map.Properties)
        {
            if (!RootKeys.Contains(property.Key) && !property.Key.StartsWith("x-", StringComparison.Ordinal))
            {
                throw new PathPolicyException("Compose document contains an unknown key.");
            }

            switch (property.Key)
            {
                case "services":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Services);
                    break;
                case "include":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Include);
                    break;
                case "volumes":
                case "configs":
                case "secrets":
                    ValidateComposeNode(property.Value, ComposeValidationContext.TopLevelResources);
                    break;
                default:
                    ValidateComposeNode(property.Value, ComposeValidationContext.Generic);
                    break;
            }
        }
    }

    private static void ValidateServices(ComposeNode node)
    {
        var map = RequireMap(node, "Compose services");
        foreach (var property in map.Properties)
        {
            if (property.Key.Length == 0)
            {
                throw new PathPolicyException("Compose service name is empty.");
            }

            ValidateComposeNode(property.Value, ComposeValidationContext.Service);
        }
    }

    private static void ValidateService(ComposeNode node)
    {
        var map = RequireMap(node, "Compose service");
        foreach (var property in map.Properties)
        {
            if (property.Key == "use_api_socket")
            {
                throw new PathPolicyException("Compose service use_api_socket is not allowed.");
            }

            if (!ServiceKeys.Contains(property.Key)
                && !property.Key.StartsWith("x-", StringComparison.Ordinal))
            {
                throw new PathPolicyException("Compose service contains an unknown key.");
            }

            switch (property.Key)
            {
                case "build":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Build);
                    break;
                case "extends":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Extends);
                    break;
                case "env_file":
                    ValidateComposeNode(property.Value, ComposeValidationContext.EnvFile);
                    break;
                case "include":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Include);
                    break;
                case "volumes":
                case "devices":
                    ValidateComposeNode(property.Value, ComposeValidationContext.MountCollection);
                    break;
                case "configs":
                case "secrets":
                    ValidateComposeNode(property.Value, ComposeValidationContext.ServiceResources);
                    break;
                case "develop":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Develop);
                    break;
                default:
                    ValidateGenericProperty(property);
                    break;
            }
        }
    }

    private static void ValidateGeneric(ComposeNode node)
    {
        switch (node)
        {
            case ComposeMap map:
                foreach (var property in map.Properties)
                {
                    ValidateGenericProperty(property);
                }

                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    ValidateGeneric(item);
                }

                break;
            case ComposeScalar:
                break;
            default:
                throw new PathPolicyException("Compose file contains an unsupported value.");
        }
    }

    private static void ValidateGenericProperty(ComposeProperty property)
    {
        if (property.Key == "use_api_socket")
        {
            throw new PathPolicyException("Compose service use_api_socket is not allowed.");
        }

        if (property.Key == "<<")
        {
            throw new PathPolicyException("Compose merge keys are not supported safely.");
        }

        switch (property.Key)
        {
            case "build":
                ValidateComposeNode(property.Value, ComposeValidationContext.Build);
                break;
            case "extends":
                ValidateComposeNode(property.Value, ComposeValidationContext.Extends);
                break;
            case "env_file":
                ValidateComposeNode(property.Value, ComposeValidationContext.EnvFile);
                break;
            case "include":
                ValidateComposeNode(property.Value, ComposeValidationContext.Include);
                break;
            case "volumes":
            case "devices":
                ValidateComposeNode(property.Value, ComposeValidationContext.MountCollection);
                break;
            case "configs":
            case "secrets":
                ValidateComposeNode(property.Value, ComposeValidationContext.ServiceResources);
                break;
            case "additional_contexts":
                ValidateComposeNode(property.Value, ComposeValidationContext.AdditionalContexts);
                break;
            case "cache_from":
            case "cache_to":
            case "outputs":
                ValidateComposeNode(property.Value, ComposeValidationContext.Cache);
                break;
            case "ssh":
                ValidateComposeNode(property.Value, ComposeValidationContext.Ssh);
                break;
            case "develop":
                ValidateComposeNode(property.Value, ComposeValidationContext.Develop);
                break;
            default:
                if (PathKeys.Contains(property.Key))
                {
                    ValidatePathNode(property.Value, property.Key);
                }
                else
                {
                    ValidateGeneric(property.Value);
                }

                break;
        }
    }

    private static void ValidateExtends(ComposeNode node)
    {
        var map = RequireMap(node, "Compose extends");
        var serviceFound = false;
        foreach (var property in map.Properties)
        {
            if (!ExtendsKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose extends contains an unknown key.");
            }

            if (property.Key == "file")
            {
                ValidatePathNode(property.Value, "extends.file");
            }
            else
            {
                ValidateServiceReference(property.Value);
                serviceFound = true;
            }
        }

        if (!serviceFound)
        {
            throw new PathPolicyException("Compose extends.service is required.");
        }
    }

    private static void ValidateBuild(ComposeNode node)
    {
        if (node is ComposeScalar)
        {
            ValidatePathNode(node, "build");
            return;
        }

        var map = RequireMap(node, "Compose build");
        foreach (var property in map.Properties)
        {
            if (!BuildKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose build contains an unknown key.");
            }

            switch (property.Key)
            {
                case "context":
                case "dockerfile":
                    ValidatePathNode(property.Value, "build." + property.Key);
                    break;
                case "additional_contexts":
                    ValidateComposeNode(property.Value, ComposeValidationContext.AdditionalContexts);
                    break;
                case "cache_from":
                case "cache_to":
                case "outputs":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Cache);
                    break;
                case "ssh":
                    ValidateComposeNode(property.Value, ComposeValidationContext.Ssh);
                    break;
                default:
                    ValidateGenericProperty(property);
                    break;
            }
        }
    }

    private static void ValidateEnvFile(ComposeNode node)
    {
        switch (node)
        {
            case ComposeScalar:
                ValidatePathNode(node, "env_file");
                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    ValidateComposeNode(item, ComposeValidationContext.EnvFileItem);
                }

                break;
            case ComposeMap:
                ValidateComposeNode(node, ComposeValidationContext.EnvFileItem);
                break;
            default:
                throw new PathPolicyException("Compose env_file has an unsupported value.");
        }
    }

    private static void ValidateEnvFileItem(ComposeNode node)
    {
        if (node is ComposeScalar)
        {
            ValidatePathNode(node, "env_file");
            return;
        }

        var map = RequireMap(node, "Compose env_file entry");
        var pathFound = false;
        foreach (var property in map.Properties)
        {
            if (!EnvFileKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose env_file contains an unknown key.");
            }

            if (property.Key == "path")
            {
                ValidatePathNode(property.Value, "env_file.path");
                pathFound = true;
            }
            else
            {
                ValidateGeneric(property.Value);
            }
        }

        if (!pathFound)
        {
            throw new PathPolicyException("Compose env_file.path is required.");
        }
    }

    private static void ValidateInclude(ComposeNode node)
    {
        switch (node)
        {
            case ComposeScalar:
                ValidatePathNode(node, "include");
                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    ValidateComposeNode(item, ComposeValidationContext.IncludeItem);
                }

                break;
            case ComposeMap:
                ValidateComposeNode(node, ComposeValidationContext.IncludeItem);
                break;
            default:
                throw new PathPolicyException("Compose include has an unsupported value.");
        }
    }

    private static void ValidateIncludeItem(ComposeNode node)
    {
        if (node is ComposeScalar)
        {
            ValidatePathNode(node, "include");
            return;
        }

        var map = RequireMap(node, "Compose include entry");
        var pathFound = false;
        foreach (var property in map.Properties)
        {
            if (!IncludeKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose include contains an unknown key.");
            }

            switch (property.Key)
            {
                case "path":
                    ValidatePathNode(property.Value, "include.path", allowSequence: true);
                    pathFound = true;
                    break;
                case "env_file":
                    ValidateComposeNode(property.Value, ComposeValidationContext.EnvFile);
                    break;
                case "project_directory":
                    ValidatePathNode(property.Value, "include.project_directory");
                    break;
                default:
                    throw new PathPolicyException("Compose include contains an unsupported key.");
            }
        }

        if (!pathFound)
        {
            throw new PathPolicyException("Compose include.path is required.");
        }
    }

    private static void ValidateMountCollection(ComposeNode node)
    {
        switch (node)
        {
            case ComposeScalar:
                ValidateMountValue(RequireScalar(node, "Compose mount").Value);
                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    ValidateComposeNode(item, ComposeValidationContext.MountItem);
                }

                break;
            case ComposeMap:
                ValidateComposeNode(node, ComposeValidationContext.MountItem);
                break;
            default:
                throw new PathPolicyException("Compose mount list has an unsupported value.");
        }
    }

    private static void ValidateMountItem(ComposeNode node)
    {
        if (node is ComposeScalar)
        {
            ValidateMountValue(RequireScalar(node, "Compose mount").Value);
            return;
        }

        var map = RequireMap(node, "Compose mount entry");
        foreach (var property in map.Properties)
        {
            if (!MountKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose mount contains an unknown key.");
            }

            switch (property.Key)
            {
                case "source":
                case "device":
                    ValidatePathNode(property.Value, "mount." + property.Key);
                    break;
                case "bind":
                case "volume":
                case "tmpfs":
                case "image":
                    ValidateMountOptions(property.Value, property.Key);
                    break;
                default:
                    ValidateGeneric(property.Value);
                    break;
            }
        }
    }

    private static void ValidateMountOptions(ComposeNode node)
    {
        ValidateMountOptions(node, "mount");
    }

    private static void ValidateMountOptions(ComposeNode node, string optionName)
    {
        var map = RequireMap(node, "Compose " + optionName + " mount options");
        var allowedKeys = optionName switch
        {
            "bind" => BindMountKeys,
            "volume" => VolumeMountKeys,
            "tmpfs" => TmpfsMountKeys,
            "image" => ImageMountKeys,
            _ => MountKeys
        };

        foreach (var property in map.Properties)
        {
            if (!allowedKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose mount options contain an unknown key.");
            }

            ValidateGeneric(property.Value);
        }
    }

    private static void ValidateTopLevelResources(ComposeNode node)
    {
        var map = RequireMap(node, "Compose top-level resources");
        foreach (var property in map.Properties)
        {
            ValidateComposeNode(property.Value, ComposeValidationContext.ResourceDefinition);
        }
    }

    private static void ValidateResourceDefinition(ComposeNode node)
    {
        var map = RequireMap(node, "Compose resource definition");
        foreach (var property in map.Properties)
        {
            if (!ResourceDefinitionKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose resource definition contains an unknown key.");
            }

            if (property.Key == "file")
            {
                ValidatePathNode(property.Value, "resource.file");
            }
            else
            {
                ValidateGeneric(property.Value);
            }
        }
    }

    private static void ValidateServiceResources(ComposeNode node)
    {
        switch (node)
        {
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    ValidateComposeNode(item, ComposeValidationContext.ServiceResourceItem);
                }

                break;
            case ComposeMap:
                ValidateComposeNode(node, ComposeValidationContext.ServiceResourceItem);
                break;
            default:
                throw new PathPolicyException("Compose service resources have an unsupported value.");
        }
    }

    private static void ValidateServiceResourceItem(ComposeNode node)
    {
        if (node is ComposeScalar)
        {
            ValidateGeneric(node);
            return;
        }

        var map = RequireMap(node, "Compose service resource");
        var sourceFound = false;
        foreach (var property in map.Properties)
        {
            if (!ServiceResourceKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose service resource contains an unknown key.");
            }

            if (property.Key == "source")
            {
                ValidateGeneric(property.Value);
                sourceFound = true;
            }
            else
            {
                ValidateGeneric(property.Value);
            }
        }

        if (!sourceFound)
        {
            throw new PathPolicyException("Compose service resource.source is required.");
        }
    }

    private static void ValidateAdditionalContexts(ComposeNode node)
    {
        switch (node)
        {
            case ComposeMap map:
                foreach (var property in map.Properties)
                {
                    ValidatePathNode(property.Value, "additional_contexts." + property.Key);
                }

                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    var scalar = RequireScalar(item, "Compose additional_contexts entry");
                    ValidateAdditionalContextValue(scalar.Value);
                }

                break;
            case ComposeScalar:
                ValidateAdditionalContextValue(RequireScalar(node, "Compose additional_contexts").Value);
                break;
            default:
                throw new PathPolicyException("Compose additional_contexts has an unsupported value.");
        }
    }

    private static void ValidateCache(ComposeNode node)
    {
        switch (node)
        {
            case ComposeScalar:
                ValidatePathValue(RequireScalar(node, "Compose cache reference").Value);
                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    ValidateComposeNode(item, ComposeValidationContext.CacheItem);
                }

                break;
            case ComposeMap:
                ValidateComposeNode(node, ComposeValidationContext.CacheItem);
                break;
            default:
                throw new PathPolicyException("Compose cache reference has an unsupported value.");
        }
    }

    private static void ValidateCacheItem(ComposeNode node)
    {
        if (node is ComposeScalar)
        {
            ValidatePathValue(RequireScalar(node, "Compose cache reference").Value);
            return;
        }

        var map = RequireMap(node, "Compose cache reference");
        foreach (var property in map.Properties)
        {
            if (!CacheKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose cache reference contains an unknown key.");
            }

            if (property.Key is "src" or "dest")
            {
                ValidatePathNode(property.Value, "cache." + property.Key);
            }
            else
            {
                ValidateGeneric(property.Value);
            }
        }
    }

    private static void ValidateSsh(ComposeNode node)
    {
        switch (node)
        {
            case ComposeScalar:
                ValidatePathNode(node, "build.ssh");
                break;
            case ComposeSequence sequence:
                foreach (var item in sequence.Items)
                {
                    ValidatePathNode(item, "build.ssh");
                }

                break;
            case ComposeMap map:
                foreach (var property in map.Properties)
                {
                    ValidatePathNode(property.Value, "build.ssh." + property.Key);
                }

                break;
            default:
                throw new PathPolicyException("Compose build.ssh has an unsupported value.");
        }
    }

    private static void ValidateDevelop(ComposeNode node)
    {
        var map = RequireMap(node, "Compose develop");
        foreach (var property in map.Properties)
        {
            if (!DevelopKeys.Contains(property.Key))
            {
                throw new PathPolicyException("Compose develop contains an unknown key.");
            }

            ValidateComposeNode(property.Value, ComposeValidationContext.Watch);
        }
    }

    private static void ValidateWatch(ComposeNode node)
    {
        var sequence = node as ComposeSequence
            ?? throw new PathPolicyException("Compose develop.watch must be a sequence.");
        foreach (var item in sequence.Items)
        {
            var map = RequireMap(item, "Compose develop.watch entry");
            var pathFound = false;
            foreach (var property in map.Properties)
            {
                if (!WatchKeys.Contains(property.Key))
                {
                    throw new PathPolicyException("Compose develop.watch contains an unknown key.");
                }

                if (property.Key == "path")
                {
                    ValidatePathNode(property.Value, "develop.watch.path");
                    pathFound = true;
                }
                else if (property.Key is "ignore" or "include")
                {
                    ValidateWatchPatternNode(property.Value, "develop.watch." + property.Key);
                }
                else
                {
                    ValidateGeneric(property.Value);
                }
            }

            if (!pathFound)
            {
                throw new PathPolicyException("Compose develop.watch.path is required.");
            }
        }
    }

    private static void ValidateWatchPatternNode(ComposeNode node, string propertyName)
    {
        if (node is ComposeSequence sequence)
        {
            foreach (var item in sequence.Items)
            {
                ValidateWatchPatternNode(item, propertyName);
            }

            return;
        }

        var scalar = RequireScalar(node, propertyName);
        if (scalar.IsBlockScalar || scalar.IsNull || scalar.Value.Length == 0)
        {
            throw new PathPolicyException("Compose watch patterns must be non-empty scalar values.");
        }

        var pattern = scalar.Value;
        if (pattern.Any(char.IsControl)
            || pattern.Contains('$')
            || pattern.Contains('\\')
            || pattern.Contains(':')
            || pattern.StartsWith('~')
            || Path.IsPathFullyQualified(pattern))
        {
            throw new PathPolicyException("Compose watch pattern is not a safe relative glob.");
        }

        var segments = pattern.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new PathPolicyException("Compose watch pattern is not a safe relative glob.");
        }
    }

    private static void ValidatePathNode(ComposeNode node, string propertyName, bool allowSequence = false)
    {
        if (allowSequence && node is ComposeSequence sequence)
        {
            foreach (var item in sequence.Items)
            {
                ValidatePathNode(item, propertyName);
            }

            return;
        }

        var scalar = RequireScalar(node, propertyName);
        if (scalar.IsBlockScalar || scalar.IsNull || scalar.Value.Length == 0)
        {
            throw new PathPolicyException("Compose path expressions must be single scalar values.");
        }

        ValidatePathValue(scalar.Value);
    }

    private static void ValidateServiceReference(ComposeNode node)
    {
        var scalar = RequireScalar(node, "extends.service");
        if (scalar.IsBlockScalar
            || string.IsNullOrWhiteSpace(scalar.Value)
            || !ServiceName.IsMatch(scalar.Value))
        {
            throw new PathPolicyException("Compose extends.service is invalid.");
        }
    }

    private static ComposeScalar RequireScalar(ComposeNode node, string propertyName)
    {
        return node as ComposeScalar
            ?? throw new PathPolicyException("Compose " + propertyName + " must be a scalar value.");
    }

    private static ComposeMap RequireMap(ComposeNode node, string propertyName)
    {
        return node as ComposeMap
            ?? throw new PathPolicyException("Compose " + propertyName + " must be a mapping.");
    }

    private static string FindComposeFileName(SafeDirectoryHandle workspace)
    {
        try
        {
            using var compose = workspace.OpenRegularFile("compose.yaml");
            return "compose.yaml";
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryNotFound)
        {
            using var compose = workspace.OpenRegularFile("compose.yml");
            return "compose.yml";
        }
    }

    private static void ValidatePathValue(string value)
    {
        var trimmedValue = value.TrimStart();
        if (trimmedValue.StartsWith('|') || trimmedValue.StartsWith('>'))
        {
            throw new PathPolicyException("Compose file contains an unresolved path expression.");
        }

        if (value.Any(char.IsControl)
            || value.Contains('$')
            || value.Contains('\\')
            || value.Contains('*'))
        {
            throw new PathPolicyException("Compose file contains an unresolved or unsafe path expression.");
        }

        foreach (var candidate in value
            .Split([',', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = candidate.Trim().Trim('"', '\'');
            if (path.Length == 0 || path is "{}" or "[]")
            {
                continue;
            }

            if (path.StartsWith('-'))
            {
                path = path[1..].Trim();
            }

            if (ContainsUnsafePathToken(path))
            {
                throw new PathPolicyException("Compose file references a path outside the private workspace.");
            }

            if (path.Length == 0
                || Path.IsPathFullyQualified(path)
                || path.StartsWith('~')
                || path.Split('/', StringSplitOptions.None).Any(segment => segment is ".." or ""))
            {
                throw new PathPolicyException("Compose file references a path outside the private workspace.");
            }

            var fileName = path[(path.LastIndexOf('/') + 1)..];
            if (IsSensitiveEnvironmentFile(fileName))
            {
                throw new PathPolicyException("Compose file references a sensitive environment file.");
            }
        }
    }

    private static void ValidateBuildValue(string value)
    {
        ValidatePathValue(value);
    }

    private static void ValidateMountValue(string value)
    {
        ValidatePathValue(value);

        foreach (var candidate in value
            .Split([',', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var mount = candidate.Trim().Trim('"', '\'');
            if (mount.Length == 0 || mount is "{}" or "[]")
            {
                continue;
            }

            if (mount.StartsWith('{'))
            {
                ValidatePathValue(mount);
                continue;
            }

            var separator = mount.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var source = mount[..separator].Trim().Trim('"', '\'');
            if (source.Length > 0)
            {
                ValidatePathValue(source);
            }
        }
    }

    private static void ValidateAdditionalContextValue(string value)
    {
        var trimmedValue = value.TrimStart();
        if (trimmedValue.StartsWith('|') || trimmedValue.StartsWith('>'))
        {
            throw new PathPolicyException("Compose file contains an unresolved build context expression.");
        }

        if (value.Contains('$')
            || value.Contains('\\')
            || value.Contains('*'))
        {
            throw new PathPolicyException("Compose file contains an unresolved or unsafe build context expression.");
        }

        ValidatePathValue(value);
        foreach (var candidate in value
            .Split([',', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var context = candidate.Trim().Trim('"', '\'');
            if (context.Length == 0 || context is "{}" or "[]")
            {
                continue;
            }

            var separator = context.IndexOf('=');
            if (separator >= 0)
            {
                context = context[(separator + 1)..].Trim();
            }

            ValidatePathValue(context);
        }
    }

    private static bool ContainsUnsafePathToken(string path)
    {
        var segments = path.Split('/', StringSplitOptions.None);
        return segments.Any(segment => segment == "..")
            || path.StartsWith('/')
            || path.StartsWith("~/", StringComparison.Ordinal)
            || path.StartsWith("~\\", StringComparison.Ordinal)
            || Regex.IsMatch(path, @"(^|[\s:=\[,])(?:[A-Za-z]:[\\/]|/|\.\.(?:[/\\]|$))", RegexOptions.CultureInvariant);
    }

    private static bool IsSensitiveEnvironmentFile(string fileName)
    {
        return fileName.Equals(".env", StringComparison.Ordinal)
            || fileName.StartsWith(".env.", StringComparison.Ordinal)
            || fileName.Equals(".secret.env", StringComparison.Ordinal)
            || fileName.StartsWith(".secret.env.", StringComparison.Ordinal);
    }
}
