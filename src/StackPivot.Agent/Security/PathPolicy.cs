using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StackPivot.Contracts.SignalR;

namespace StackPivot.Agent.Security;

public sealed record SafePath(
    string FullPath,
    string RelativePath,
    SafeDirectoryHandle? DirectoryHandle = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        DirectoryHandle?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SafeDirectoryHandle : IDisposable
{
    private SafeFileHandle? handle;

    internal SafeDirectoryHandle(SafeFileHandle handle, string canonicalPath)
    {
        this.handle = handle ?? throw new ArgumentNullException(nameof(handle));
        CanonicalPath = canonicalPath;
    }

    internal string CanonicalPath { get; }

    internal string ProcessWorkingDirectory
    {
        get
        {
            var fileDescriptor = GetFileDescriptor();
            return $"/proc/self/fd/{fileDescriptor}";
        }
    }

    internal IReadOnlyList<string> EnumerateEntryNames(int maxEntries = int.MaxValue)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);

        var names = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(ProcessWorkingDirectory))
        {
            var name = Path.GetFileName(entry);
            if (name is null)
            {
                continue;
            }

            names.Add(name);
            if (names.Count > maxEntries)
            {
                throw new PathPolicyException("Directory contains too many entries.");
            }
        }

        return names;
    }

    internal static SafeDirectoryHandle OpenOrCreateAbsoluteDirectory(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!Path.IsPathFullyQualified(absolutePath))
        {
            throw new PathPolicyException("Directory path must be absolute.");
        }

        var canonicalPath = Path.GetFullPath(absolutePath);
        var directoryHandle = LinuxPathOperations.OpenDirectoryTree(canonicalPath, Array.Empty<string>());
        return new SafeDirectoryHandle(directoryHandle, canonicalPath);
    }

    public FileStream OpenFile(string relativePath, FileMode mode, FileAccess access)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        var fileHandle = LinuxPathOperations.OpenFileAt(GetFileDescriptor(), relativePath, mode, access);
        return new FileStream(fileHandle, access, bufferSize: 4096, isAsync: false);
    }

    internal FileStream OpenRegularFile(string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        var fileHandle = LinuxPathOperations.OpenRegularFileAt(GetFileDescriptor(), name);
        return new FileStream(fileHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
    }

    internal SafeDirectoryHandle? TryOpenChildDirectory(string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return OpenChildDirectory(name, create: false);
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryNotFound)
        {
            return null;
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.NotDirectory)
        {
            return null;
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.SymbolicLinkLoop)
        {
            return null;
        }
    }

    internal SafeDirectoryHandle OpenChildDirectory(string name, bool create, bool inheritHandle = false)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        var child = LinuxPathOperations.OpenDirectoryAt(
            GetFileDescriptor(),
            name,
            create,
            closeOnExec: !inheritHandle);
        return new SafeDirectoryHandle(child, Path.Combine(CanonicalPath, name));
    }

    internal void ValidateManagedFilePath(string relativePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        try
        {
            LinuxPathOperations.ValidateManagedPath(GetFileDescriptor(), relativePath);
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryNotFound)
        {
            // A file that read-tree will materialize later is valid.
        }
    }

    internal void DeleteFile(string relativePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        LinuxPathOperations.DeleteFileAt(GetFileDescriptor(), relativePath);
    }

    internal FileStream OpenLockFile(string relativePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        var fileHandle = LinuxPathOperations.OpenLockFileAt(GetFileDescriptor(), relativePath);
        return new FileStream(fileHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
    }

    internal void DeleteContents()
    {
        foreach (var name in EnumerateEntryNames())
        {
            if (name is "." or "..")
            {
                throw new PathPolicyException("Directory enumeration returned an unsafe entry.");
            }

            var child = TryOpenChildDirectory(name);
            if (child is not null)
            {
                using (child)
                {
                    child.DeleteContents();
                }

                LinuxPathOperations.DeleteDirectoryAt(GetFileDescriptor(), name);
            }
            else
            {
                DeleteFile(name);
            }
        }
    }

    internal void DeleteChildDirectory(string name)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        LinuxPathOperations.DeleteDirectoryAt(GetFileDescriptor(), name);
    }

    internal void EnsurePrivateDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        LinuxPathOperations.EnsurePrivateDirectory(GetFileDescriptor());
    }

    internal static TemporaryDirectory CreateTemporaryDirectory(string prefix, bool inheritFinalHandle = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        var parentPath = Path.Combine(Path.GetTempPath(), "stackpivot-private");
        var parent = OpenOrCreateAbsoluteDirectory(parentPath);
        try
        {
            parent.EnsurePrivateDirectory();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var name = $"{prefix}{Guid.NewGuid():N}";
                SafeDirectoryHandle? child = null;
                try
                {
                    child = parent.OpenChildDirectory(name, create: true, inheritHandle: inheritFinalHandle);
                    child.EnsurePrivateDirectory();
                    return new TemporaryDirectory(child, parent, name, Path.Combine(parentPath, name));
                }
                catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryExists)
                {
                    child?.Dispose();
                }
                catch
                {
                    child?.Dispose();
                    try
                    {
                        parent.DeleteChildDirectory(name);
                    }
                    catch (PathPolicyException cleanupException) when (cleanupException.ErrorNumber == LinuxPathOperations.EntryNotFound)
                    {
                    }

                    throw;
                }
            }

            throw new PathPolicyException("Unable to create a unique private directory.");
        }
        catch
        {
            parent.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref handle, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    private int GetFileDescriptor()
    {
        var current = Volatile.Read(ref handle);
        ObjectDisposedException.ThrowIf(current is null || current.IsClosed || current.IsInvalid, this);

        return checked((int)current.DangerousGetHandle().ToInt64());
    }
}

internal sealed class TemporaryDirectory(
    SafeDirectoryHandle directory,
    SafeDirectoryHandle parent,
    string name,
    string fullPath) : IDisposable
{
    public SafeDirectoryHandle Directory { get; } = directory;
    public string FullPath { get; } = fullPath;

    public void Dispose()
    {
        try
        {
            Directory.DeleteContents();
            parent.DeleteChildDirectory(name);
        }
        finally
        {
            Directory.Dispose();
            parent.Dispose();
        }
    }
}

public sealed class PathPolicy
{
    private readonly string configuredRoot;

    public PathPolicy(string configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);
        if (!Path.IsPathFullyQualified(configuredRoot))
        {
            throw new PathPolicyException("Agent root must be an absolute path.");
        }

        var normalizedRoot = Path.GetFullPath(configuredRoot);
        this.configuredRoot = normalizedRoot.Length > 1
            ? normalizedRoot.TrimEnd(Path.DirectorySeparatorChar)
            : normalizedRoot;
    }

    public Task<SafePath> ValidateStackPathAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Validate(relativePath));
    }

    public Task<SafePath> OpenStackPathAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safePath = Validate(relativePath);
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Agent path operations require Linux.");
        }

        try
        {
            var segments = relativePath.Split('/', StringSplitOptions.None);
            var handle = LinuxPathOperations.OpenDirectoryTree(configuredRoot, segments, inheritFinalHandle: true);
            return Task.FromResult(safePath with
            {
                DirectoryHandle = new SafeDirectoryHandle(handle, safePath.FullPath)
            });
        }
        catch (PathPolicyException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            throw new PathPolicyException("Unable to open the agent stack directory safely.", exception);
        }
    }

    public Task<SafePath> OpenStackPathAsync(
        string configuredRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        return string.Equals(NormalizeRoot(configuredRoot), this.configuredRoot, StringComparison.Ordinal)
            ? OpenStackPathAsync(relativePath, cancellationToken)
            : new PathPolicy(configuredRoot).OpenStackPathAsync(relativePath, cancellationToken);
    }

    public Task<SafePath> ValidateStackPathAsync(
        string configuredRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        return string.Equals(NormalizeRoot(configuredRoot), this.configuredRoot, StringComparison.Ordinal)
            ? ValidateStackPathAsync(relativePath, cancellationToken)
            : new PathPolicy(configuredRoot).ValidateStackPathAsync(relativePath, cancellationToken);
    }

    public Task<string> ValidateManagedFilePathAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateManagedPathSyntax(relativePath);

        var segments = relativePath.Split('/', StringSplitOptions.None);
        var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), configuredRoot);
        if (!IsUnderRoot(fullPath))
        {
            throw new PathPolicyException("Managed path escapes the agent root.");
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                LinuxPathOperations.ValidateManagedPath(configuredRoot, segments);
            }
            catch (PathPolicyException exception) when (exception.ErrorNumber == LinuxPathOperations.EntryNotFound)
            {
                // A not-yet-materialized file is valid; its parent components were checked.
            }
        }
        else
        {
            EnsureNoSymlinkEscape(segments);
        }

        return Task.FromResult(fullPath);
    }

    private SafePath Validate(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Any(character => char.IsControl(character) || character == '\\' || character == ':')
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new PathPolicyException("Stack path must be relative and contain no control characters.");
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Length != 2
            || segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or "..")
            || segments.Any(segment => !ProtocolValidation.IsSafeName(segment)))
        {
            throw new PathPolicyException("Stack path contains an unsafe segment.");
        }

        var fullPath = Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), configuredRoot);
        if (!IsUnderRoot(fullPath))
        {
            throw new PathPolicyException("Stack path escapes the agent root.");
        }

        EnsureNoSymlinkEscape(segments);
        return new SafePath(fullPath, relativePath);
    }

    private static void ValidateManagedPathSyntax(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Any(character => char.IsControl(character) || character is '\\' or ':' or '\0')
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new PathPolicyException("Managed path must be relative and contain no control characters.");
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or ".." or ".git"))
        {
            throw new PathPolicyException("Managed path contains an unsafe segment.");
        }
    }

    private bool IsUnderRoot(string fullPath)
    {
        var rootWithSeparator = configuredRoot.EndsWith(Path.DirectorySeparatorChar)
            ? configuredRoot
            : configuredRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private void EnsureNoSymlinkEscape(IReadOnlyList<string> segments)
    {
        EnsureDirectoryIsNotSymlink(configuredRoot);
        var current = configuredRoot;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var entry = new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                throw new PathPolicyException("Stack path contains a symbolic link.");
            }

            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null)
            {
                throw new PathPolicyException("Stack path contains a symbolic link.");
            }
        }
    }

    private static void EnsureDirectoryIsNotSymlink(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            throw new PathPolicyException("Agent root contains a symbolic link.");
        }
    }

    private static string NormalizeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path);
        return normalized.Length > 1
            ? normalized.TrimEnd(Path.DirectorySeparatorChar)
            : normalized;
    }
}

internal static class LinuxPathOperations
{
    private const int AtCurrentWorkingDirectory = -100;
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenExclusive = 0x80;
    private const int OpenTruncate = 0x200;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int OpenAppend = 0x400;
    private const int OpenNonBlock = 0x800;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x000007ff;
    private const ushort StatxTypeMask = 0xf000;
    private const ushort StatxTypeRegular = 0x8000;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int RemoveDirectory = 0x200;
    private const uint DirectoryMode = 0x1C0;
    private const uint FileMode = 0x180;
    private const uint PrivateDirectoryMode = 0x1C0;

    internal const int EntryNotFound = 2;
    internal const int EntryExists = 17;
    internal const int NotDirectory = 20;
    internal const int SymbolicLinkLoop = 40;
    internal const int ResourceBusy = 11;

    internal static void EnsurePrivateDirectory(int fileDescriptor)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("File descriptor relative paths require Linux.");
        }

        if (fchmod(fileDescriptor, PrivateDirectoryMode) != 0)
        {
            throw CreateNativeException("Unable to restrict a private directory.", Marshal.GetLastWin32Error());
        }

        var mode = File.GetUnixFileMode($"/proc/self/fd/{fileDescriptor}");
        const UnixFileMode permissionBits = UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        if ((mode & permissionBits) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
        {
            throw new PathPolicyException("Private directory permissions could not be verified.");
        }
    }

    internal static SafeFileHandle OpenDirectoryTree(
        string absoluteRoot,
        IReadOnlyList<string> relativeSegments,
        bool inheritFinalHandle = false)
    {
        var current = OpenRootDirectory();
        try
        {
            var allSegments = GetAbsoluteSegments(absoluteRoot).Concat(relativeSegments).ToArray();
            for (var index = 0; index < allSegments.Length; index++)
            {
                var next = OpenDirectoryAt(
                    GetFileDescriptor(current),
                    allSegments[index],
                    create: true,
                    closeOnExec: !inheritFinalHandle || index != allSegments.Length - 1);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle OpenDirectoryAt(
        int parentFileDescriptor,
        string name,
        bool create,
        bool closeOnExec = true)
    {
        ValidateSingleSegment(name, allowGitDirectory: true);
        var openFlags = OpenReadOnly | OpenDirectory | OpenNoFollow;
        if (closeOnExec)
        {
            openFlags |= OpenCloseOnExec;
        }

        var fileDescriptor = openat(
            parentFileDescriptor,
            name,
            openFlags,
            0);
        if (fileDescriptor < 0 && create && Marshal.GetLastWin32Error() == EntryNotFound)
        {
            var mkdirResult = mkdirat(parentFileDescriptor, name, DirectoryMode);
            var mkdirError = Marshal.GetLastWin32Error();
            if (mkdirResult < 0 && mkdirError != EntryExists)
            {
                throw CreateNativeException("Unable to create a managed directory.", mkdirError);
            }

            fileDescriptor = openat(
                parentFileDescriptor,
                name,
                openFlags,
                0);
        }

        if (fileDescriptor < 0)
        {
            throw CreateNativeException("Unable to open a managed directory.", Marshal.GetLastWin32Error());
        }

        return new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
    }

    internal static SafeFileHandle OpenFileAt(
        int parentFileDescriptor,
        string relativePath,
        System.IO.FileMode mode,
        System.IO.FileAccess access)
    {
        var segments = ValidateManagedSegments(relativePath);
        SafeFileHandle? current = null;
        var currentFileDescriptor = parentFileDescriptor;
        try
        {
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var next = OpenDirectoryAt(currentFileDescriptor, segments[index], create: true);
                current?.Dispose();
                current = next;
                currentFileDescriptor = GetFileDescriptor(current);
            }

            var flags = GetFileFlags(mode, access);
            var fileDescriptor = openat(currentFileDescriptor, segments[^1], flags, FileMode);
            if (fileDescriptor < 0)
            {
                throw CreateNativeException("Unable to open a managed file.", Marshal.GetLastWin32Error());
            }

            return new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
        }
        finally
        {
            current?.Dispose();
        }
    }

    internal static SafeFileHandle OpenRegularFileAt(int parentFileDescriptor, string name)
    {
        ValidateSingleSegment(name, allowGitDirectory: false);
        var fileDescriptor = openat(
            parentFileDescriptor,
            name,
            OpenReadOnly | OpenNoFollow | OpenCloseOnExec | OpenNonBlock,
            0);
        if (fileDescriptor < 0)
        {
            throw CreateNativeException("Unable to open a managed regular file.", Marshal.GetLastWin32Error());
        }

        var fileHandle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
        try
        {
            if (!IsRegularFile(fileDescriptor))
            {
                throw new PathPolicyException("Managed path must reference a regular file.");
            }

            return fileHandle;
        }
        catch
        {
            fileHandle.Dispose();
            throw;
        }
    }

    internal static void ValidateManagedPath(string absoluteRoot, IReadOnlyList<string> segments)
    {
        using var root = OpenAbsoluteDirectory(absoluteRoot);
        ValidateManagedPath(GetFileDescriptor(root), segments);
    }

    internal static void ValidateManagedPath(int rootFileDescriptor, string relativePath)
    {
        ValidateManagedPath(rootFileDescriptor, ValidateManagedSegments(relativePath));
    }

    internal static void ValidateManagedPath(int rootFileDescriptor, IReadOnlyList<string> segments)
    {
        SafeFileHandle? current = null;
        var currentFileDescriptor = rootFileDescriptor;
        try
        {
            for (var index = 0; index < segments.Count; index++)
            {
                var isFinal = index == segments.Count - 1;
                var fileDescriptor = openat(
                    currentFileDescriptor,
                    segments[index],
                    isFinal
                        ? OpenReadOnly | OpenNoFollow | OpenCloseOnExec
                        : OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
                    0);
                if (fileDescriptor < 0)
                {
                    throw CreateNativeException("Unable to validate a managed path.", Marshal.GetLastWin32Error());
                }

                var next = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
                current?.Dispose();
                current = next;
                currentFileDescriptor = GetFileDescriptor(current);
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    internal static void DeleteFileAt(int rootFileDescriptor, string relativePath)
    {
        var segments = ValidateManagedSegments(relativePath);
        SafeFileHandle? current = null;
        var currentFileDescriptor = rootFileDescriptor;
        try
        {
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var next = OpenDirectoryAt(currentFileDescriptor, segments[index], create: false);
                current?.Dispose();
                current = next;
                currentFileDescriptor = GetFileDescriptor(current);
            }

            var result = unlinkat(currentFileDescriptor, segments[^1], 0);
            if (result < 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == EntryNotFound)
                {
                    return;
                }

                throw CreateNativeException("Unable to delete a managed file.", error);
            }
        }
        catch (PathPolicyException exception) when (exception.ErrorNumber == EntryNotFound)
        {
            // A stale file or parent disappearing is already the desired state.
        }
        finally
        {
            current?.Dispose();
        }
    }

    internal static void DeleteDirectoryAt(int parentFileDescriptor, string name)
    {
        ValidateSingleSegment(name, allowGitDirectory: true);
        var result = unlinkat(parentFileDescriptor, name, RemoveDirectory);
        if (result < 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != EntryNotFound)
            {
                throw CreateNativeException("Unable to delete a managed directory.", error);
            }
        }
    }

    internal static SafeFileHandle OpenLockFileAt(int parentFileDescriptor, string relativePath)
    {
        var segments = ValidateManagedSegments(relativePath);
        if (segments.Length != 1)
        {
            throw new PathPolicyException("Lock file path must contain one segment.");
        }

        var fileDescriptor = openat(
            parentFileDescriptor,
            segments[0],
            OpenReadWrite | OpenCreate | OpenNoFollow | OpenCloseOnExec,
            FileMode);
        if (fileDescriptor < 0)
        {
            throw CreateNativeException("Unable to open a deployment lock file.", Marshal.GetLastWin32Error());
        }

        var fileHandle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
        if (flock(fileDescriptor, LockExclusive | LockNonBlocking) < 0)
        {
            var errorNumber = Marshal.GetLastWin32Error();
            fileHandle.Dispose();
            throw CreateNativeException("Unable to acquire a deployment lock file.", errorNumber);
        }

        return fileHandle;
    }

    private static SafeFileHandle OpenAbsoluteDirectory(string absoluteRoot)
    {
        var current = OpenRootDirectory();
        try
        {
            foreach (var segment in GetAbsoluteSegments(absoluteRoot))
            {
                var next = OpenDirectoryAt(GetFileDescriptor(current), segment, create: false);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenRootDirectory()
    {
        var fileDescriptor = openat(
            AtCurrentWorkingDirectory,
            "/",
            OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec,
            0);
        if (fileDescriptor < 0)
        {
            throw CreateNativeException("Unable to open the filesystem root.", Marshal.GetLastWin32Error());
        }

        return new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
    }

    private static string[] GetAbsoluteSegments(string absoluteRoot)
    {
        if (!Path.IsPathFullyQualified(absoluteRoot))
        {
            throw new PathPolicyException("Agent root must be an absolute path.");
        }

        return absoluteRoot.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] ValidateManagedSegments(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath.Any(character => char.IsControl(character) || character is '\\' or ':' or '\0')
            || Path.IsPathFullyQualified(relativePath))
        {
            throw new PathPolicyException("Managed path must be relative and contain no control characters.");
        }

        var segments = relativePath.Split('/', StringSplitOptions.None);
        foreach (var segment in segments)
        {
            ValidateSingleSegment(segment, allowGitDirectory: false);
        }

        return segments;
    }

    private static void ValidateSingleSegment(string segment, bool allowGitDirectory)
    {
        if (string.IsNullOrEmpty(segment)
            || segment is "." or ".."
            || (!allowGitDirectory && segment == ".git")
            || segment.Any(character => char.IsControl(character) || character is '/' or '\\' or ':' or '\0'))
        {
            throw new PathPolicyException("Managed path contains an unsafe segment.");
        }
    }

    private static int GetFileFlags(System.IO.FileMode mode, System.IO.FileAccess access)
    {
        var flags = access switch
        {
            System.IO.FileAccess.Read => OpenReadOnly,
            System.IO.FileAccess.Write => OpenWriteOnly,
            System.IO.FileAccess.ReadWrite => OpenReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(access))
        };
        flags |= OpenNoFollow | OpenCloseOnExec;
        flags |= mode switch
        {
            System.IO.FileMode.CreateNew => OpenCreate | OpenExclusive,
            System.IO.FileMode.Create => OpenCreate | OpenTruncate,
            System.IO.FileMode.Open => 0,
            System.IO.FileMode.OpenOrCreate => OpenCreate,
            System.IO.FileMode.Truncate => OpenTruncate,
            System.IO.FileMode.Append => OpenCreate | OpenAppend,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        return flags;
    }

    private static int GetFileDescriptor(SafeFileHandle handle)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed || handle.IsInvalid, handle);

        return checked((int)handle.DangerousGetHandle().ToInt64());
    }

    private static bool IsRegularFile(int fileDescriptor)
    {
        if (statx(
                fileDescriptor,
                string.Empty,
                AtEmptyPath,
                StatxBasicStats,
                out var metadata) != 0)
        {
            throw CreateNativeException("Unable to inspect a managed file.", Marshal.GetLastWin32Error());
        }

        return (metadata.Mode & StatxTypeMask) == StatxTypeRegular;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 256)]
    private struct LinuxStatx
    {
        public uint Mask;
        public uint BlockSize;
        public ulong Attributes;
        public uint LinkCount;
        public uint UserId;
        public uint GroupId;
        public ushort Mode;
        public ushort Spare0;
    }

    private static PathPolicyException CreateNativeException(string message, int errorNumber)
    {
        return new PathPolicyException($"{message} (errno {errorNumber}).", errorNumber);
    }

    #pragma warning disable CA2101
    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int openat(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        int flags,
        uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int mkdirat(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        uint mode);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unlinkat(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int flock(int fileDescriptor, int operation);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int fchmod(int fileDescriptor, uint mode);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int statx(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPStr)] string path,
        int flags,
        uint mask,
        out LinuxStatx metadata);
    #pragma warning restore CA2101
}

public sealed class PathPolicyException : Exception
{
    public PathPolicyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    internal PathPolicyException(string message, int errorNumber)
        : base(message)
    {
        ErrorNumber = errorNumber;
    }

    internal int? ErrorNumber { get; }
}
