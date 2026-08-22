using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Vaultix.Shared;

namespace Vaultix.Service;

/// <summary>
/// Creates the local IPC pipes with an ACL that permits the signed-in desktop
/// user to talk to the Windows service (which itself runs as LocalSystem).
/// </summary>
internal static class VaultixPipeServer
{
    public static NamedPipeServerStream CreateCommandPipe() => Create(
        VaultixProtocol.PipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        64 * 1024,
        64 * 1024,
        PipeAccessRights.ReadWrite);

    public static NamedPipeServerStream CreateStatusPipe() => Create(
        VaultixProtocol.StatusPipeName,
        PipeDirection.Out,
        1,
        0,
        128 * 1024,
        PipeAccessRights.ReadData);

    private static NamedPipeServerStream Create(
        string name,
        PipeDirection direction,
        int instances,
        int inputBufferSize,
        int outputBufferSize,
        PipeAccessRights clientAccess)
    {
        var security = new PipeSecurity();
        var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        security.AddAccessRule(new PipeAccessRule(authenticatedUsers, clientAccess, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            name,
            direction,
            instances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inputBufferSize,
            outputBufferSize,
            security);
    }
}
