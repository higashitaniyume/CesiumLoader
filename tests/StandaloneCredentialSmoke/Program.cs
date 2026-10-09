using StandaloneSpectator;
using System.Text;
using System.Security.Cryptography;
if(!OperatingSystem.IsWindows()){Console.WriteLine("SKIP Windows-only DPAPI test");return;}
var root=Path.Combine(Path.GetTempPath(),"StandaloneCredentialSmoke",Guid.NewGuid().ToString("N"));
var path=Path.Combine(root,"auth.protected");
var original=new AuthHandoff("fake",DateTime.UtcNow,"localhost",1234,"test","FAKE_SECRET_NOT_REAL",2,0,0);
var first=new CredentialStore(path);first.Save(original);
if(Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(original.ConnectRequestBase64))throw new Exception("Plaintext authentication found");
var loaded=new CredentialStore(path).Load();if(loaded!=original)throw new Exception("Roundtrip mismatch");
var bytes=File.ReadAllBytes(path);bytes[^1]^=1;File.WriteAllBytes(path,bytes);
bool rejected=false;try{new CredentialStore(path).Load();}catch(CryptographicException){rejected=true;}
if(!rejected)throw new Exception("Tampered cache accepted");
Console.WriteLine("PASS current-user encrypted cache, fresh instance roundtrip, no plaintext marker, tamper rejection; synthetic credentials only.");
