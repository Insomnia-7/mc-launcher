using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Launcher.Application.Abstractions;

namespace Launcher.Infrastructure.Security;

/// <summary>Only the encryption key enters the OS keychain; account files retain authenticated ciphertext.</summary>
public sealed class SystemKeySecretProtector(Func<bool,byte[]?> keyProvider,string description) : ISecretProtector
{
    private static readonly byte[] Header="FYKEY1"u8.ToArray();
    public static ISecretProtector? TryCreate(string dataDirectory)
    {
        if(OperatingSystem.IsWindows())return DpapiSecretProtector.TryCreate();
        var account=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))));
        if(OperatingSystem.IsMacOS())return new SystemKeySecretProtector(create=>MacKeychain.Key(account,create),"macOS Keychain / AES-GCM");
        if(OperatingSystem.IsLinux()&&FindSecretTool() is {} tool&&Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS") is {Length:>0})
            return new SystemKeySecretProtector(create=>LinuxKey(tool,account,create),"Linux Secret Service / AES-GCM");
        return null;
    }
    public byte[] Protect(string plaintext)
    {
        var key=keyProvider(true)??throw new CryptographicException("系统密钥环不可用或未解锁，账号未保存。请解锁系统密钥环后重试。");
        try
        {
            var plain=Encoding.UTF8.GetBytes(plaintext);var result=new byte[Header.Length+12+16+plain.Length];Header.CopyTo(result,0);
            RandomNumberGenerator.Fill(result.AsSpan(Header.Length,12));
            using var aes=new AesGcm(key,16);aes.Encrypt(result.AsSpan(Header.Length,12),plain,result.AsSpan(Header.Length+28),result.AsSpan(Header.Length+12,16),Header);
            CryptographicOperations.ZeroMemory(plain);return result;
        }
        finally{CryptographicOperations.ZeroMemory(key);}
    }
    public string? TryUnprotect(ReadOnlySpan<byte> payload)
    {
        if(payload.Length<Header.Length+28||!payload[..Header.Length].SequenceEqual(Header))return null;
        try
        {
            var key=keyProvider(false);if(key is null)return null;
            try{using var aes=new AesGcm(key,16);var plain=new byte[payload.Length-Header.Length-28];aes.Decrypt(payload.Slice(Header.Length,12),payload[(Header.Length+28)..],payload.Slice(Header.Length+12,16),plain,Header);try{return Encoding.UTF8.GetString(plain);}finally{CryptographicOperations.ZeroMemory(plain);}}
            finally{CryptographicOperations.ZeroMemory(key);}
        }
        catch(Exception ex) when(ex is CryptographicException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception){return null;}
    }
    public string Describe()=>description;
    private static string? FindSecretTool()=>(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Select(p=>Path.Combine(p,"secret-tool")).FirstOrDefault(File.Exists);
    private static byte[]? LinuxKey(string tool,string account,bool create)
    {
        string? Run(string action,string? input=null)
        {
            var start=new ProcessStartInfo(tool){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,CreateNoWindow=true};
            start.ArgumentList.Add(action);if(action=="store"){start.ArgumentList.Add("--label=方隅账号加密密钥");}
            foreach(var arg in new[]{"application","org.fangyu.launcher","installation",account})start.ArgumentList.Add(arg);
            using var process=Process.Start(start)??throw new IOException("无法启动系统密钥环。");
            var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            if(input is not null)process.StandardInput.Write(input);process.StandardInput.Close();
            if(!process.WaitForExit(30000)){process.Kill();throw new IOException("系统密钥环响应超时，请确认密钥环已解锁。");}
            Task.WaitAll(output,error);
            if(process.ExitCode!=0){if(action=="lookup"&&string.IsNullOrWhiteSpace(error.Result))return null;throw new IOException("系统密钥环不可用，请解锁密钥环并安装 libsecret-tools。");}
            return output.Result.TrimEnd('\r','\n');
        }
        var existing=Run("lookup");if(existing is {Length:>0})return DecodeKey(existing);
        if(!create)return null;
        var generated=RandomNumberGenerator.GetBytes(32);Run("store",Convert.ToBase64String(generated));
        var persisted=DecodeKey(Run("lookup")??"");
        if(persisted is null||!CryptographicOperations.FixedTimeEquals(generated,persisted)){CryptographicOperations.ZeroMemory(generated);throw new CryptographicException("无法确认系统密钥已保存。");}
        CryptographicOperations.ZeroMemory(generated);return persisted;
    }
    private static byte[]? DecodeKey(string value){try{var key=Convert.FromBase64String(value);return key.Length==32?key:null;}catch(FormatException){return null;}}

    private static class MacKeychain
    {
        private const string Security="/System/Library/Frameworks/Security.framework/Security",Core="/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private static readonly nint Library=NativeLibrary.Load(Security);
        private static nint Constant(string name)=>Marshal.ReadIntPtr(NativeLibrary.GetExport(Library,name));
        [DllImport(Core)]private static extern nint CFStringCreateWithCString(nint allocator,[MarshalAs(UnmanagedType.LPUTF8Str)]string value,uint encoding);
        [DllImport(Core)]private static extern nint CFDictionaryCreateMutable(nint allocator,nint capacity,nint keys,nint values);
        [DllImport(Core)]private static extern void CFDictionarySetValue(nint dictionary,nint key,nint value);
        [DllImport(Core)]private static extern nint CFDataCreate(nint allocator,byte[] data,nint length);
        [DllImport(Core)]private static extern nint CFDataGetLength(nint data);
        [DllImport(Core)]private static extern nint CFDataGetBytePtr(nint data);
        [DllImport(Core)]private static extern void CFRelease(nint value);
        [DllImport(Security)]private static extern int SecItemCopyMatching(nint query,out nint result);
        [DllImport(Security)]private static extern int SecItemAdd(nint attributes,nint result);
        public static byte[]? Key(string account,bool create)
        {
            var service=CFStringCreateWithCString(0,"org.fangyu.launcher.accounts",0x08000100);var name=CFStringCreateWithCString(0,account,0x08000100);var query=CFDictionaryCreateMutable(0,0,0,0);
            try
            {
                CFDictionarySetValue(query,Constant("kSecClass"),Constant("kSecClassGenericPassword"));CFDictionarySetValue(query,Constant("kSecAttrService"),service);CFDictionarySetValue(query,Constant("kSecAttrAccount"),name);
                var search=CFDictionaryCreateMutable(0,0,0,0);
                try
                {
                    CFDictionarySetValue(search,Constant("kSecClass"),Constant("kSecClassGenericPassword"));CFDictionarySetValue(search,Constant("kSecAttrService"),service);CFDictionarySetValue(search,Constant("kSecAttrAccount"),name);
                    var cf=NativeLibrary.Load(Core);CFDictionarySetValue(search,Constant("kSecReturnData"),Marshal.ReadIntPtr(NativeLibrary.GetExport(cf,"kCFBooleanTrue")));NativeLibrary.Free(cf);
                    var status=SecItemCopyMatching(search,out var result);
                    if(status==0){try{var length=(int)CFDataGetLength(result);if(length!=32)throw new CryptographicException("系统密钥长度无效。");var key=new byte[length];Marshal.Copy(CFDataGetBytePtr(result),key,0,length);return key;}finally{CFRelease(result);}}
                    if(status!=-25300)throw new CryptographicException($"钥匙串不可用（{status}），请解锁登录钥匙串并允许方隅访问。");
                    if(!create)return null;
                }
                finally{CFRelease(search);}
                var generated=RandomNumberGenerator.GetBytes(32);var data=CFDataCreate(0,generated,generated.Length);
                try{CFDictionarySetValue(query,Constant("kSecValueData"),data);var status=SecItemAdd(query,0);if(status==0)return generated;CryptographicOperations.ZeroMemory(generated);if(status==-25299)return Key(account,false);throw new CryptographicException($"保存钥匙串失败（{status}）。");}finally{CFRelease(data);}
            }
            finally{CFRelease(query);CFRelease(name);CFRelease(service);}
        }
    }
}
