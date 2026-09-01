using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text;

namespace StackPivot.Agent.Security;

public sealed class CredentialTransportUnavailableException(string message) : Exception(message);

public sealed class InMemoryGitCredential : IDisposable
{
    private const uint MemfdCloseOnExec = 0x0001;
    private SafeFileHandle? handle;
    private byte[]? token;

    private InMemoryGitCredential(SafeFileHandle handle, byte[] token)
    {
        this.handle = handle;
        this.token = token;
        AskpassPath = $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}";
    }

    public string AskpassPath { get; }

    public static InMemoryGitCredential Create(string userName, byte[] accessToken)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(accessToken);
        if (!OperatingSystem.IsLinux())
        {
            throw new CredentialTransportUnavailableException("Linux memfd is required for Git credentials.");
        }

        var name = Encoding.UTF8.GetBytes("stackpivot-askpass\0");
        var fileDescriptor = memfd_create(name, 0);
        CryptographicOperations.ZeroMemory(name);
        if (fileDescriptor < 0)
        {
            throw new CredentialTransportUnavailableException("memfd_create is unavailable.");
        }

        var safeHandle = new SafeFileHandle((IntPtr)fileDescriptor, ownsHandle: true);
        var tokenCopy = accessToken.ToArray();
        try
        {
            var script = Encoding.UTF8.GetBytes(BuildScript(userName, tokenCopy));
            RandomAccess.Write(safeHandle, script, 0);

            CryptographicOperations.ZeroMemory(script);
            return new InMemoryGitCredential(safeHandle, tokenCopy);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(tokenCopy);
            safeHandle.Dispose();
            throw new CredentialTransportUnavailableException("Unable to create the in-memory askpass program.");
        }
    }

    public void Dispose()
    {
        if (token is not null)
        {
            CryptographicOperations.ZeroMemory(token);
            token = null;
        }

        handle?.Dispose();
        handle = null;
        GC.SuppressFinalize(this);
    }

    private static string BuildScript(string userName, byte[] accessToken)
    {
        var token = Encoding.UTF8.GetString(accessToken);
        return "#!/bin/sh\ncase \"$1\" in\n*Username*) printf '%s\\n' '"
            + EscapeSingleQuoted(userName)
            + "' ;;\n*) printf '%s\\n' '"
            + EscapeSingleQuoted(token)
            + "' ;;\nesac\n";
    }

    private static string EscapeSingleQuoted(string value)
    {
        return value.Replace("'", "'\\''", StringComparison.Ordinal);
    }

    [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true)]
    private static extern int memfd_create(byte[] name, uint flags);
}
