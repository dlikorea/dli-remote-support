using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class UpdateClient : IDisposable
{
    private const int MaxSetupBytes = 30 * 1024 * 1024;
    private readonly HttpClient http;

    public UpdateClient(Uri server)
    {
        if (server.Scheme != Uri.UriSchemeHttps && !(server.Scheme == Uri.UriSchemeHttp && server.IsLoopback))
            throw new InvalidOperationException("HTTPS 업데이트 주소가 필요합니다.");
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        http.BaseAddress = server;
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<UpdateManifest> Check(CancellationToken token)
    {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(3500);
            using (HttpResponseMessage response = await http.GetAsync("/updates/manifest.json", HttpCompletionOption.ResponseHeadersRead, deadline.Token))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                using (var data = new MemoryStream())
                {
                    await Copy(response.Content, data, 8192, deadline.Token);
                    var manifest = new JavaScriptSerializer { MaxJsonLength = 8192 }.Deserialize<UpdateManifest>(Encoding.UTF8.GetString(data.ToArray()));
                    Version next = Validate(manifest);
                    return next > Product.Release ? manifest : null;
                }
            }
        }
    }

    private static Version Validate(UpdateManifest manifest)
    {
        Version version;
        if (manifest == null || !Version.TryParse(manifest.version, out version) || version.Revision < 0
            || version.ToString() != manifest.version || manifest.size < 1 || manifest.size > MaxSetupBytes
            || manifest.sha256 == null || !Regex.IsMatch(manifest.sha256, "\\A[0-9a-f]{64}\\z")
            || String.IsNullOrEmpty(manifest.signature))
            throw new InvalidDataException("업데이트 정보의 형식이 올바르지 않습니다.");

        string message = "DLI.RemoteSupport\n" + manifest.version + "\n"
            + manifest.size.ToString(CultureInfo.InvariantCulture) + "\n" + manifest.sha256 + "\n";
        using (var rsa = new RSACryptoServiceProvider(new CspParameters(24)))
        {
            rsa.PersistKeyInCsp = false;
            rsa.ImportParameters(new RSAParameters { Modulus = Convert.FromBase64String(ReleaseSettings.UpdateModulus),
                Exponent = Convert.FromBase64String(ReleaseSettings.UpdateExponent) });
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(message), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(manifest.signature)))
                throw new InvalidDataException("업데이트 서명을 확인할 수 없습니다.");
        }
        return version;
    }

    public async Task<string> Download(UpdateManifest manifest, CancellationToken token)
    {
        Version expectedVersion = Validate(manifest);
        Directory.CreateDirectory(Product.StagingPath);
        string path = Path.Combine(Product.StagingPath, "setup-" + Guid.NewGuid().ToString("N") + ".exe");
        bool verified = false;
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(45000);
                using (HttpResponseMessage response = await http.GetAsync("/updates/" + manifest.version + "/" + Product.SetupName,
                    HttpCompletionOption.ResponseHeadersRead, deadline.Token))
                {
                    response.EnsureSuccessStatusCode();
                    long? declared = response.Content.Headers.ContentLength;
                    if (declared.HasValue && declared != manifest.size) throw new InvalidDataException("업데이트 파일 크기가 다릅니다.");
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        if (await Copy(response.Content, output, manifest.size, deadline.Token) != manifest.size)
                            throw new InvalidDataException("업데이트 파일 크기가 다릅니다.");
                }
            }
            using (var file = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                if (actual != manifest.sha256) throw new InvalidDataException("업데이트 파일 검증에 실패했습니다.");
            }
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            if (new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart) != expectedVersion)
                throw new InvalidDataException("업데이트 파일 버전이 다릅니다.");
            token.ThrowIfCancellationRequested();
            verified = true;
            return path;
        }
        finally
        {
            if (!verified) File.Delete(path);
        }
    }

    private static async Task<int> Copy(HttpContent content, Stream output, int limit, CancellationToken token)
    {
        using (Stream input = await content.ReadAsStreamAsync())
        using (token.Register(input.Dispose))
        {
            var buffer = new byte[32768];
            int read, total = 0;
            while ((read = await input.ReadAsync(buffer, 0, buffer.Length, token)) != 0)
            {
                total += read;
                if (total > limit) throw new InvalidDataException("업데이트 응답이 너무 큽니다.");
                await output.WriteAsync(buffer, 0, read, token);
            }
            return total;
        }
    }

    public void Dispose() { http.Dispose(); }
}

public sealed class UpdateManifest
{
    public string version { get; set; }
    public int size { get; set; }
    public string sha256 { get; set; }
    public string signature { get; set; }
}
