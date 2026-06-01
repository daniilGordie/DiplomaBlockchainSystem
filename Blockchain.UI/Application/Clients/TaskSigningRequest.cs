using Blockchain.UI.Models;

namespace Blockchain.UI.Application.Clients;

public sealed record TaskSigningRequest(TaskEvent Event, string Keystore, string Password);
