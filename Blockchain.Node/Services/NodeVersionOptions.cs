namespace Blockchain.Node.Services;

public sealed class NodeVersionOptions
{
    public string Product { get; set; } = "Nexus";
    public string NodeVersion { get; set; } = "0.1.0";
    public string IrohSidecarVersion { get; set; } = "0.1.0";
    public string ProtocolVersion { get; set; } = "blockchain.proto:v1";
    public string IrohAlpn { get; set; } = "nexus-blockchain/iroh/1";
    public string UpdateManifestUrl { get; set; } = "";
}
