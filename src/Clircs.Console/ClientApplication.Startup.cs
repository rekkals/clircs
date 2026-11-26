using Clircs.Commands;
using Clircs.Networking;

namespace Clircs.ConsoleClient;

internal sealed partial class ClientApplication
{
    private async Task StartRequestedConnectionsAsync(
        StartupConnectionRequest request,
        IReadOnlyList<NetworkProfile> profiles)
    {
        if (request.Endpoint is { } endpoint)
        {
            var password = request.PromptForPassword
                ? _presenter.ReadSecret("Server password (Esc cancels): ")
                : null;

            if (request.PromptForPassword && password is null)
            {
                DisplayCommandResult(
                    CommandResult.Failure("Connection canceled."),
                    CaptureCommandContext());
                return;
            }

            try
            {
                var options = new IrcConnectionOptions(endpoint, CurrentIdentity(), password)
                    .Validate();
                RememberRecentConnection(options, profileId: null);
                await StartSessionAsync(options, _lifetime.Token);
            }
            catch (ArgumentException exception)
            {
                DisplayCommandResult(
                    CommandResult.Failure(exception.Message),
                    CaptureCommandContext());
            }

            return;
        }

        var setupErrors = new List<string>();
        foreach (var profile in profiles)
        {
            if (_exitRequested || _lifetime.IsCancellationRequested)
            {
                return;
            }

            IrcConnectionOptions connectionRoute;
            IrcConnectionOptions options;
            try
            {
                connectionRoute = ConnectionOptionsForProfile(profile, CurrentIdentity());
                options = ApplyProfileServerPassword(profile, connectionRoute);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or InvalidDataException or ArgumentException)
            {
                setupErrors.Add($"Could not start profile '{profile.DisplayName}'. {exception.Message}");
                continue;
            }

            RememberRecentConnection(connectionRoute, profile.Id);
            await StartSessionAsync(
                options,
                _lifetime.Token,
                profile.DisplayName,
                profile.Id,
                connectionRoute: connectionRoute);
        }

        var errorContext = CaptureCommandContext();
        foreach (var error in setupErrors)
        {
            DisplayCommandResult(CommandResult.Failure(error), errorContext);
        }
    }
}
