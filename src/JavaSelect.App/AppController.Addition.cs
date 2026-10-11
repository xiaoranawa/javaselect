using JavaSelect.Core;

namespace JavaSelect.App;

public sealed partial class AppController
{
    public async Task<JavaInstallation?> ProbeForAdditionAsync(string path, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        return await discovery.ProbeAsync(path, linked.Token);
    }
}
