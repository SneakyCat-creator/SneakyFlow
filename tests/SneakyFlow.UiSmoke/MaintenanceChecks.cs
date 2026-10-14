using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SneakyFlow;
using SneakyFlow.Services;

internal static class MaintenanceChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task RunAsync(string output)
    {
        var root = Directory.CreateDirectory(Path.Combine(output, "maintenance-" + Guid.NewGuid().ToString("N"))).FullName;
        // A portable EXE may sit directly on a drive. Validate without touching that location.
        var driveRoot = Path.GetPathRoot(root)!;
        DownloadDeletion.ValidatePath(driveRoot, Path.Combine(driveRoot, "SneakyFlow.exe"));
        await ExpectFailure(() => { DownloadDeletion.ValidatePath(driveRoot, driveRoot); return Task.CompletedTask; });
        var state = new ProductStateStore(Path.Combine(root, "profile"));
        Check(!LicensePolicy.IsAccepted(await state.ReadAsync()), "A fresh profile must not accept terms.");
        await state.AcceptAsync();
        var accepted = await new ProductStateStore(state.Root).ReadAsync();
        Check(LicensePolicy.IsAccepted(accepted), "Acceptance must survive a new store/process.");
        Check(accepted.AcceptedLicenseVersion == LicensePolicy.Version && accepted.AcceptedAt is not null, "Versioned acceptance missing.");
        Check(!LicensePolicy.IsAccepted(accepted with { AcceptedLicenseVersion = "old" }), "Old agreement accepted.");
        Check(!LicensePolicy.IsAccepted(accepted with { AcceptedLicenseHash = "old" }), "Changed terms accepted.");
        var copy = Path.Combine(root, "copy.exe");
        await File.WriteAllTextAsync(copy, "fixture");
        await state.RegisterCopyAsync(copy); await state.RegisterCopyAsync(copy);
        Check((await state.ReadAsync()).Copies.Count == 1 && LicensePolicy.IsAccepted(await state.ReadAsync()), "Copy registration reset acceptance or duplicated a path.");
        await state.RevokeAsync();
        Check(!LicensePolicy.IsAccepted(await state.ReadAsync()), "Revocation did not invalidate acceptance.");
        await File.WriteAllTextAsync(Path.Combine(state.Root, "product-state.json"), "broken JSON");
        Check(!LicensePolicy.IsAccepted(await state.ReadAsync()), "Corrupt state bypassed terms.");
        File.Delete(Path.Combine(state.Root, "product-state.json"));
        Check(!LicensePolicy.IsAccepted(await state.ReadAsync()), "Deleting state bypassed terms.");
        Console.WriteLine("PASS license version/hash acceptance, shared-copy registration, revocation and damaged/missing state");
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => new ProductStateStore(state.Root).RegisterCopyAsync(copy)));
        Check((await state.ReadAsync()).Copies.Count == 1, "Concurrent executable copies corrupted their shared state.");
        using (var lease = MaintenanceLease.Acquire(state.Root))
            Check(MaintenanceLease.IsBusy(state.Root), "An additional copy could start during maintenance.");
        Check(!MaintenanceLease.IsBusy(state.Root), "The maintenance guard was left behind.");
        Console.WriteLine("PASS concurrent shared-profile writes and automatic release of the maintenance guard");

        var consent = new AboutWindow(true);
        consent.Show();
        Check(((Button)consent.FindName("AcceptButton")).IsVisible && !((CheckBox)consent.FindName("AcceptedBox")).IsVisible,
            "Startup agreement controls are incorrect.");
        ((Button)consent.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(consent.Accepted && !consent.RemovalRequested, "Accept requested removal.");
        var decline = new AboutWindow(true); decline.Show();
        ((Button)decline.FindName("DeclineButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!decline.Accepted && decline.RemovalRequested, "Declining did not request removal.");
        var revoke = new AboutWindow(); revoke.Show();
        ((CheckBox)revoke.FindName("AcceptedBox")).IsChecked = false;
        Check(revoke.RemovalRequested, "Unchecking did not request removal.");
        var dismiss = new AboutWindow(true); dismiss.Show(); dismiss.Close();
        Check(!dismiss.Accepted && !dismiss.RemovalRequested, "Closing the dialog is not acceptance or explicit refusal.");
        Console.WriteLine("PASS startup/about acceptance controls; no removal actions executed");
        var owner = new Window { Width = 400, Height = 200 }; owner.Show();
        foreach (bool delete in new[] { false, true })
        {
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                var modal = Application.Current.Windows.OfType<Window>().Single(w => w.Owner == owner);
                var rootPanel = (StackPanel)modal.Content;
                if (delete) rootPanel.Children.OfType<RadioButton>().Last().IsChecked = true;
                var buttons = rootPanel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().ToArray();
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Check(SneakyFlow.UI.Dialogs.ChooseRemoval(owner, "Synthetic torrent") == delete, "Removal dialog returned the wrong choice.");
        }
        _ = Application.Current.Dispatcher.BeginInvoke(() => Application.Current.Windows.OfType<Window>().Single(w => w.Owner == owner).Close());
        Check(SneakyFlow.UI.Dialogs.ChooseRemoval(owner, "Synthetic torrent") is null, "Cancelling removal did not preserve the torrent.");
        owner.Close();
        Console.WriteLine("PASS keep-files/delete-files/cancel choices; UI alone never deletes data");

        var image = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "SneakyFlow.exe"));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image));
        var version = typeof(App).Assembly.GetName().Version!;
        var releaseVersion = new Version(version.Major, version.Minor, version.Build);
        var tag = "v" + releaseVersion;
        var baseUrl = $"https://github.com/{ReleaseClient.Repository}/releases/download/{tag}/";
        var assets = new[] {
            new { name="SneakyFlow.exe", browser_download_url=baseUrl+"SneakyFlow.exe", size=image.LongLength, digest="sha256:"+sha }
        };
        string releaseJson = JsonSerializer.Serialize(new { tag_name=tag, draft=false, prerelease=false, assets });
        string manifest = JsonSerializer.Serialize(new { tag, version=releaseVersion.ToString(), sha256=sha, size=image.LongLength, licenseVersion=LicensePolicy.Version });
        using var http = new HttpClient(new FakeHttp(releaseJson, manifest, image));
        var client = new ReleaseClient(http);
        var info = await client.CheckAsync(new Version(1,0,0), CancellationToken.None) ?? throw new Exception("Release not recognized.");
        foreach (var malformed in new[] { releaseJson.Replace("sha256:" + sha, ""), releaseJson.Replace("sha256:" + sha, "sha256:bad"),
            releaseJson.Replace("https://github.com/", "https://example.com/"), releaseJson.Replace("SneakyFlow.exe", "Other.exe") })
        {
            using var invalidHttp = new HttpClient(new FakeHttp(malformed, manifest, image));
            await ExpectFailure(() => new ReleaseClient(invalidHttp).CheckAsync(new Version(1, 0, 0), CancellationToken.None));
        }
        Check(await client.CheckAsync(releaseVersion, CancellationToken.None) is null, "Update would reinstall the same version.");
        Check(await client.CheckAsync(releaseVersion, CancellationToken.None, currentIsPrerelease: true) is not null,
            "A release candidate must be able to update to the stable release of the same version.");
        string prereleaseJson = JsonSerializer.Serialize(new { tag_name = tag + "-rc1", draft = false, prerelease = true, assets });
        using var prereleaseHttp = new HttpClient(new FakeHttp(prereleaseJson, manifest, image));
        Check(await new ReleaseClient(prereleaseHttp).CheckAsync(new Version(1, 0, 0), CancellationToken.None) is null,
            "Automatic updates must not install prereleases.");
        var downloaded = Path.Combine(root, "downloaded.exe");
        var downloadProgress = new List<UpdateProgress>();
        await client.DownloadAsync(info, downloaded, CancellationToken.None, new CallbackProgress<UpdateProgress>(downloadProgress.Add));
        Check(await ProductStateStore.HashAsync(downloaded) == sha, "Verified bytes changed.");
        Check(downloadProgress[0] == new UpdateProgress(UpdateStage.Downloading, 0) &&
            downloadProgress[^2] == new UpdateProgress(UpdateStage.Downloading, 100) &&
            downloadProgress[^1] == new UpdateProgress(UpdateStage.Verifying), "Download stages are out of order.");
        var percentages = downloadProgress.Where(p => p.Stage == UpdateStage.Downloading).Select(p => p.Percent!.Value).ToArray();
        Check(percentages.SequenceEqual(percentages.Order()) && percentages.Any(p => p > 0 && p < 100), "Download progress is missing or decreases.");
        await ExpectFailure(() => client.DownloadAsync(info, downloaded, CancellationToken.None));
        Check(await ProductStateStore.HashAsync(downloaded) == sha, "Retry deleted a pre-existing file.");
        await ExpectFailure(() => client.DownloadAsync(info with { Sha256 = new string('0',64) }, Path.Combine(root, "corrupt.exe"), CancellationToken.None));
        await ExpectFailure(() => client.DownloadAsync(info with { Size = 4 }, Path.Combine(root, "oversize.exe"), CancellationToken.None));
        var incompleteProgress = new List<UpdateProgress>();
        var incomplete = Path.Combine(root, "incomplete.exe");
        await ExpectFailure(() => client.DownloadAsync(info with { Size = info.Size + 100 }, incomplete, CancellationToken.None,
            new CallbackProgress<UpdateProgress>(incompleteProgress.Add)));
        Check(!File.Exists(incomplete) && incompleteProgress.All(p => p.Percent < 100), "Incomplete download was left behind or shown as 100%.");
        using (var cancellation = new CancellationTokenSource())
        {
            var partial = Path.Combine(root, "cancelled.exe");
            try
            {
                await client.DownloadAsync(info, partial, cancellation.Token,
                    new CallbackProgress<UpdateProgress>(p => { if (p.Percent > 0) cancellation.Cancel(); }));
                throw new Exception("Download ignored cancellation.");
            }
            catch (OperationCanceledException) { }
            Check(!File.Exists(partial), "Cancelled download was left behind.");
        }
        Check(!File.Exists(Path.Combine(root, "corrupt.exe")) && !File.Exists(Path.Combine(root, "oversize.exe")), "Invalid download remains launchable.");
        await ExpectFailure(() => Task.FromResult(ReleaseClient.ValidateAssetUrl("https://example.com/a.exe", tag, "SneakyFlow.exe")));
        await ExpectFailure(() => Task.FromResult(UpdateHandoff.ValidateDirectory(Path.Combine(root, "work.json"))));

        var old = Path.Combine(root, "installed.fixture");
        await File.WriteAllTextAsync(Path.Combine(root, "proceed"), "proceed");
        await UpdateHandoff.WaitForCommitAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "cancel"), "cancel");
        await ExpectFailure(() => UpdateHandoff.WaitForCommitAsync(root));
        await File.WriteAllTextAsync(old, "old version");
        var ticket = new UpdateTicket(0,0,old,await ProductStateStore.HashAsync(old),sha,releaseVersion.ToString());
        var installProgress = new List<UpdateProgress>();
        await UpdateHandoff.StageAsync(ticket, downloaded, root, new CallbackProgress<UpdateProgress>(installProgress.Add));
        var staged = UpdateHandoff.StagedPath(ticket, root);
        Check(await ProductStateStore.HashAsync(staged) == sha && await File.ReadAllTextAsync(old) == "old version", "Staging touched the running target or corrupted bytes.");
        Check(installProgress.All(p => p.Stage == UpdateStage.Installing && p.Percent <= 95) && installProgress[^1].Percent == 95 &&
            installProgress.Any(p => p.Percent > 0 && p.Percent <= 90), "Installation claimed completion before replacement.");
        await ExpectFailure(() => UpdateHandoff.StageAsync(ticket, downloaded, root));
        Check(await ProductStateStore.HashAsync(staged) == sha, "Staging deleted a file it did not create.");
        await UpdateHandoff.ReplaceAsync(ticket, downloaded, root);
        Check(await ProductStateStore.HashAsync(old) == sha, "Atomic update did not replace target.");
        Check(!File.Exists(staged), "Atomic update did not consume the staged file.");
        UpdateHandoff.RestoreBackup(ticket,root);
        Check(await File.ReadAllTextAsync(old) == "old version", "Rollback lost the original.");
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                await UpdateHandoff.StageAsync(ticket, downloaded, root,
                    new CallbackProgress<UpdateProgress>(_ => cancellation.Cancel()), cancellation.Token);
                throw new Exception("Staging ignored cancellation.");
            }
            catch (OperationCanceledException) { }
            Check(!File.Exists(staged) && await File.ReadAllTextAsync(old) == "old version", "Cancelled staging damaged the original or left a partial file.");
        }
        await UpdateHandoff.ReplaceAsync(ticket, downloaded, root);
        Check(await ProductStateStore.HashAsync(old) == sha, "Legacy handoff without pre-staging stopped working.");
        UpdateHandoff.RestoreBackup(ticket, root);
        await File.WriteAllTextAsync(staged, "unrecognized staged file");
        await ExpectFailure(() => UpdateHandoff.ReplaceAsync(ticket, downloaded, root));
        Check(await File.ReadAllTextAsync(staged) == "unrecognized staged file" && await File.ReadAllTextAsync(old) == "old version", "Unrecognized staged content was installed or deleted.");
        File.Delete(staged);
        await File.WriteAllTextAsync(old,"changed during update");
        await ExpectFailure(() => UpdateHandoff.ReplaceAsync(ticket,downloaded,root));
        Check(await File.ReadAllTextAsync(old) == "changed during update", "Updater overwrote a changed target.");
        Console.WriteLine("PASS official release parsing, hash/size checks, URL restrictions, atomic update, rollback and changed-target refusal");
    }
    private static async Task ExpectFailure(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; } throw new Exception("Unsafe operation was accepted."); }
    private sealed class FakeHttp(string release, string manifest, byte[] image) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath.EndsWith("SneakyFlow.exe")
                ? new ByteArrayContent(image) : new StringContent(request.RequestUri.AbsolutePath.EndsWith("release.json") ? manifest : release, Encoding.UTF8, "application/json") });
    }
}
