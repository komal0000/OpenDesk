using System.Security.Cryptography;
using OpenDesk.Core;
using OpenDesk.Service;
using static OpenDesk.Tests.Program;

namespace OpenDesk.Tests;

internal static class DirectoryIntegrationTests
{
    internal static void Register()
    {
        Test("Numeric ID lookup over pinned HTTPS reaches the correct approved host", async () => {
            using var cert = SecureSession.CreateCertificate(); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string path = Path.Combine(Path.GetTempPath(),"opendesk-dir-" + Guid.NewGuid().ToString("N"),"devices.json");
            await using var service = await DirectoryHost.StartAsync(path,cert,0,0,true);
            using var directory = new DirectoryClient(service.Url,SecureSession.Pin(cert));
            var info = await directory.InfoAsync(default);
            int approvals = 0;
            await using var host = new HostService(() => new(8,8,[255,216,255,217]),new RecordingInput(),(_,_) => { approvals++; return Task.FromResult(new Approval(true)); });
            await host.StartRelayAsync(new Uri(service.Url).Host,info.RelayPort,true);
            var address = await directory.RegisterAsync(key,host.Invitation,host.RelayOwner,default);
            var resolved = await directory.LookupAsync(DeviceProof.FormatId(address.Id),default);
            Check(approvals == 0 && resolved.Session.Pin == host.Invitation.Pin);
            await using var viewer = await ViewerSession.ConnectAsync(resolved.Session,"ID viewer",default);
            Check(approvals == 1 && host.Coordinator.Viewers.Count == 1);
            using var wrong = new DirectoryClient(service.Url,new string('0',64));
            await Throws<HttpRequestException>(() => wrong.InfoAsync(default));
            await host.DisposeAsync();
            await Throws<IOException>(() => directory.LookupAsync(address.Id,default));
            if (File.Exists(path)) File.Delete(path);
        });
    }
}
