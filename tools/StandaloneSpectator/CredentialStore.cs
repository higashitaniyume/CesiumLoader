using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;

namespace StandaloneSpectator;

/// <summary>Opt-in local Windows-user encrypted cache; never expose plaintext through HTTP.</summary>
public sealed class CredentialStore
{
    readonly string path;
    readonly IDataProtector protector;
    public CredentialStore(string path)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Credential cache requires Windows current-user protection.");
        this.path=Path.GetFullPath(path);
        var directory=Path.GetDirectoryName(this.path)!;Directory.CreateDirectory(directory);
        var provider=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory,"keys")),
            options=>{ options.SetApplicationName("CesiumStandaloneSpectator");
                if(OperatingSystem.IsWindows())options.ProtectKeysWithDpapi();
                else throw new PlatformNotSupportedException("Windows protection required"); });
        protector=provider.CreateProtector("OwnAccountAuthCache.v1");
    }
    public bool Exists=>File.Exists(path);
    public void Save(AuthHandoff auth)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(auth);
        try
        {
            var encrypted=protector.Protect(bytes);var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllBytes(temp,encrypted);File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
        }
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    public AuthHandoff Load()
    {
        var data=File.ReadAllBytes(path);if(data.Length>128*1024)throw new InvalidDataException("Encrypted cache too large");
        var bytes=protector.Unprotect(data);
        try{return JsonSerializer.Deserialize<AuthHandoff>(bytes)??throw new InvalidDataException("Invalid protected cache");}
        finally{CryptographicOperations.ZeroMemory(bytes);}
    }
}
