using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CadreLink;
// A short-lived local download, opened only while the pairing dialog is showing the APK code.
internal sealed class PhoneInstaller : IDisposable {
    readonly CancellationTokenSource lifetime=new(TimeSpan.FromMinutes(5));
    readonly TcpListener listener;
    readonly string path="/"+Convert.ToHexString(RandomNumberGenerator.GetBytes(16))+"/CadreLink.apk";
    readonly string apk=Path.Combine(AppContext.BaseDirectory,"CadreLink-Android.apk");
    public string Url {get;}
    public PhoneInstaller(IPAddress ip){
        if(!File.Exists(apk))throw new FileNotFoundException("L’APK manque dans le dossier de CadreLink.");
        listener=new(ip,47473);listener.Start(3);Url=$"http://{ip}:47473{path}";_=Serve();
    }
    async Task Serve(){
        try{while(!lifetime.IsCancellationRequested){using var c=await listener.AcceptTcpClientAsync(lifetime.Token);
            using var request=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);request.CancelAfter(8000);
            try{using var stream=c.GetStream();byte[] head=new byte[8192];int n=0;
                while(n<head.Length){int read=await stream.ReadAsync(head.AsMemory(n),request.Token);if(read==0)break;n+=read;if(head.AsSpan(0,n).IndexOf("\r\n\r\n"u8)>=0)break;}
                string line=Encoding.ASCII.GetString(head,0,n).Split('\r')[0];
                if(!line.StartsWith("GET "+path+" HTTP/1.",StringComparison.Ordinal)){await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),request.Token);continue;}
                await using var file=File.OpenRead(apk);var headers=$"HTTP/1.1 200 OK\r\nContent-Type: application/vnd.android.package-archive\r\nContent-Disposition: attachment; filename=CadreLink-Android.apk\r\nContent-Length: {file.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),request.Token);await file.CopyToAsync(stream,request.Token);
            }catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException){}
        }}catch(Exception ex)when(lifetime.IsCancellationRequested && ex is OperationCanceledException or SocketException or ObjectDisposedException){}
        finally{listener.Stop();}
    }
    public void Dispose(){lifetime.Cancel();listener.Stop();}
}
