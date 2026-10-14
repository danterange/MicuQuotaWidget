using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MicuQuotaWidget.Services;

/// <summary>
/// Saves the API key in Windows Credential Manager rather than a project file, environment dump, or plaintext JSON setting.
/// </summary>
public sealed class WindowsCredentialStore : IApiKeyStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string TargetName = "MicuQuotaWidget.ApiKey";

    /// <summary>
    /// Reads the current user's credential and returns no value when it has not been configured.
    /// </summary>
    public string? ReadApiKey()
    {
        if (!CredRead(TargetName, CredentialTypeGeneric, 0, out IntPtr credentialPointer))
        {
            int errorCode = Marshal.GetLastWin32Error();

            return errorCode == ErrorNotFound
                ? null
                : throw new Win32Exception(errorCode, "无法读取 Windows Credential Manager 中的 Micu API Key。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);

            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            byte[] bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    /// <summary>
    /// Writes the key as a current-user generic credential and wipes the temporary unmanaged buffer before releasing it.
    /// </summary>
    public void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API Key 不能为空。", nameof(apiKey));
        }

        byte[] secretBytes = Encoding.Unicode.GetBytes(apiKey.Trim());
        IntPtr secretPointer = Marshal.AllocCoTaskMem(secretBytes.Length);

        try
        {
            Marshal.Copy(secretBytes, 0, secretPointer, secretBytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = checked((uint)secretBytes.Length),
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存 Windows Credential Manager 中的 Micu API Key。");
            }
        }
        finally
        {
            Array.Clear(secretBytes);
            ZeroMemory(secretPointer, secretBytes.Length);
            Marshal.FreeCoTaskMem(secretPointer);
        }
    }

    /// <summary>
    /// Deletes the current user's generic credential and treats an already-missing entry as a successful reset.
    /// </summary>
    public void DeleteApiKey()
    {
        if (CredDelete(TargetName, CredentialTypeGeneric, 0))
        {
            return;
        }

        int errorCode = Marshal.GetLastWin32Error();

        if (errorCode != ErrorNotFound)
        {
            throw new Win32Exception(errorCode, "无法删除 Windows Credential Manager 中的 Micu API Key。");
        }
    }

    /// <summary>
    /// Clears a temporary unmanaged secret buffer before it is returned to the operating system allocator.
    /// </summary>
    private static void ZeroMemory(IntPtr pointer, int length)
    {
        for (int index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }
    }

    /// <summary>
    /// Reads one generic Windows credential by target name.
    /// </summary>
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    /// <summary>
    /// Writes one generic Windows credential for the current user.
    /// </summary>
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    /// <summary>
    /// Deletes one generic Windows credential by target name.
    /// </summary>
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    /// <summary>
    /// Frees an unmanaged credential buffer allocated by Credential Manager.
    /// </summary>
    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>
    /// Mirrors the native CREDENTIALW layout required by Windows Credential Manager interop.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
