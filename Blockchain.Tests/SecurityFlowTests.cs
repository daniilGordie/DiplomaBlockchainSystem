using Blockchain.Core;
using Blockchain.Core.Contracts;
using Blockchain.Infrastructure.Persistence;
using Blockchain.Infrastructure.Services;
using Blockchain.Node.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Reflection;
using Xunit;

namespace Blockchain.Tests;

public sealed class SecurityFlowTests
{
    [Fact]
    public void GitWebhookSecurity_ShouldValidateHmacAndPayloadFields()
    {
        string secret = "test-webhook-secret";
        string payload = "{\"Repository\":\"team/repo\",\"CommitHash\":\"abcdef1234\",\"ProjectId\":\"Project_1\"}";
        string signature = GitWebhookSecurity.ComputeHmacSha256(payload, secret);

        Assert.True(GitWebhookSecurity.IsValidSignature(payload, secret, $"sha256={signature}"));
        Assert.False(GitWebhookSecurity.IsValidSignature(payload, secret, "sha256=00"));
        Assert.False(GitWebhookSecurity.IsValidSignature(payload + "x", secret, $"sha256={signature}"));

        Assert.True(GitWebhookSecurity.IsValidCommitHash("abcdef1234"));
        Assert.True(GitWebhookSecurity.IsValidProjectId("Project_1"));
        Assert.True(GitWebhookSecurity.IsValidRepository("team/repo"));

        Assert.False(GitWebhookSecurity.IsValidCommitHash("not-a-hash"));
        Assert.False(GitWebhookSecurity.IsValidProjectId("../Project"));
        Assert.False(GitWebhookSecurity.IsValidRepository("repo with spaces"));
    }

    [Fact]
    public void TaskContract_ShouldRequireRealProjectMemberAssigneeForActiveStatuses()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_task_assignee_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "AssigneeProj";
            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var contract = new TaskContract();

            Assert.False(contract.Validate(
                $"{{\"Type\":\"Move\",\"TaskId\":\"T1\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"Status\":1,\"Assignee\":\"None\"}}",
                "alice-pk",
                db));

            Assert.False(contract.Validate(
                $"{{\"Type\":\"Move\",\"TaskId\":\"T1\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"Status\":2,\"Assignee\":\"Bob\"}}",
                "alice-pk",
                db));

            db.SaveBlock(new Block
            {
                Index = 1,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"TargetPublicKey\":\"bob-pk\",\"Role\":\"Developer\"}}",
                PreviousHash = "prev",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            Assert.True(contract.Validate(
                $"{{\"Type\":\"Move\",\"TaskId\":\"T1\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"Status\":1,\"Assignee\":\"Bob\"}}",
                "alice-pk",
                db));
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void VerifySignature_ShouldRejectInvalidSignatureLength()
    {
        var block = new Block
        {
            Index = 1,
            Timestamp = DateTime.UtcNow,
            Data = "{\"Type\":\"Create\",\"TaskId\":\"T1\"}",
            PreviousHash = "abc",
            ValidatorPublicKey = Convert.ToBase64String(ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo()),
            Signature = Convert.ToBase64String(new byte[32]) // invalid for p1363 ECDSA (must be 64 bytes)
        };

        bool ok = block.VerifySignature();

        Assert.False(ok);
    }

    [Fact]
    public void VerifySignature_ShouldAcceptValidP1363Signature()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        DateTime ts = DateTime.UtcNow;
        string data = "{\"Type\":\"Create\",\"TaskId\":\"T-OK\"}";
        string prevHash = "prev-hash";
        int index = 2;

        string signableData = $"{index}{ts:O}{data}{prevHash}";
        byte[] signatureBytes = ecdsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(signableData),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var block = new Block
        {
            Index = index,
            Timestamp = ts,
            Data = data,
            PreviousHash = prevHash,
            ValidatorPublicKey = publicKey,
            Signature = Convert.ToBase64String(signatureBytes)
        };

        Assert.True(block.VerifySignature());
    }

    [Fact]
    public void ProcessPeerBlock_ShouldAcceptUnixTimestampSignedBlock()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_unix_timestamp_{Guid.NewGuid():N}.db");
        try
        {
            var manager = CreateBlockchainManager(dbPath);
            var latest = manager.GetLatestBlock("System");
            Assert.NotNull(latest);

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            long unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            DateTime timestamp = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            string data = "{\"Type\":\"CreateProject\",\"ProjectId\":\"UnixTimeProj\",\"User\":\"Alice\"}";
            string prevHash = latest!.Hash;
            int index = latest.Index + 1;
            string signableData = $"{index}{unixSeconds}{data}{prevHash}";

            byte[] signatureBytes = ecdsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(signableData),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            var peerBlock = new Block
            {
                Index = index,
                Timestamp = timestamp,
                TimestampUnixSeconds = unixSeconds,
                Data = data,
                PreviousHash = prevHash,
                ValidatorPublicKey = publicKey,
                Signature = Convert.ToBase64String(signatureBytes),
                ChannelId = "System"
            };
            BlockchainManager.FinalizeBlock(peerBlock);

            bool accepted = manager.ProcessPeerBlock(peerBlock);

            Assert.True(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void ProcessPeerBlock_ShouldRejectHashContentMismatch()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_test_{Guid.NewGuid():N}.db");
        try
        {
            var manager = CreateBlockchainManager(dbPath);
            var latest = manager.GetLatestBlock("System");
            Assert.NotNull(latest);

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            DateTime ts = DateTime.UtcNow;
            string data = "{\"Type\":\"CreateProject\",\"ProjectId\":\"SecureProj\"}";
            string prevHash = latest!.Hash;
            int index = latest.Index + 1;
            string signableData = $"{index}{ts:O}{data}{prevHash}";

            byte[] signatureBytes = ecdsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(signableData),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            var peerBlock = new Block
            {
                Index = index,
                Timestamp = ts,
                Data = data,
                PreviousHash = prevHash,
                ValidatorPublicKey = publicKey,
                Signature = Convert.ToBase64String(signatureBytes),
                Nonce = 0,
                Hash = "000000INVALID_HASH_PAYLOAD_MISMATCH",
                ChannelId = "System"
            };

            bool accepted = manager.ProcessPeerBlock(peerBlock);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void ProcessPeerBlock_ShouldRejectProjectScopedPayloadInSystemChannel()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_channel_guard_system_{Guid.NewGuid():N}.db");
        try
        {
            var manager = CreateBlockchainManager(dbPath);
            var latest = manager.GetLatestBlock("System");
            Assert.NotNull(latest);

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            DateTime ts = DateTime.UtcNow;
            string data = "{\"Type\":\"Create\",\"TaskId\":\"T-1\",\"ProjectId\":\"Alpha\",\"User\":\"Alice\"}";
            string prevHash = latest!.Hash;
            int index = latest.Index + 1;
            string signableData = $"{index}{ts:O}{data}{prevHash}";

            byte[] signatureBytes = ecdsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(signableData),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            var peerBlock = new Block
            {
                Index = index,
                Timestamp = ts,
                Data = data,
                PreviousHash = prevHash,
                ValidatorPublicKey = publicKey,
                Signature = Convert.ToBase64String(signatureBytes),
                Nonce = 0,
                Hash = "placeholder",
                ChannelId = "System"
            };

            bool accepted = manager.ProcessPeerBlock(peerBlock);
            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void ProcessPeerBlock_ShouldRejectMismatchedProjectChannel()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_channel_guard_mismatch_{Guid.NewGuid():N}.db");
        try
        {
            var manager = CreateBlockchainManager(dbPath);
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

            DateTime ts = DateTime.UtcNow;
            string data = "{\"Type\":\"Create\",\"TaskId\":\"T-2\",\"ProjectId\":\"Alpha\",\"User\":\"Alice\"}";
            string prevHash = "0";
            int index = 0;
            string signableData = $"{index}{ts:O}{data}{prevHash}";

            byte[] signatureBytes = ecdsa.SignData(
                System.Text.Encoding.UTF8.GetBytes(signableData),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            var peerBlock = new Block
            {
                Index = index,
                Timestamp = ts,
                Data = data,
                PreviousHash = prevHash,
                ValidatorPublicKey = publicKey,
                Signature = Convert.ToBase64String(signatureBytes),
                Nonce = 0,
                Hash = "placeholder",
                ChannelId = "Beta"
            };

            bool accepted = manager.ProcessPeerBlock(peerBlock);
            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void LoadChain_ShouldUseSameSanitizedChannelNameAsSaveBlock()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_channel_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "Team_alpha_123";

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"Create\",\"TaskId\":\"T1\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            var chain = db.LoadChain(projectId);

            Assert.Single(chain);
            Assert.Equal(projectId.Replace("_", ""), chain[0].ChannelId);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void AccessControl_ShouldRejectGovernanceVoteFromNonMember()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_governance_rbac_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "GovernanceProj";
            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var contract = new AccessControlContract();
            bool accepted = contract.Validate(
                $"{{\"Type\":\"CastVote\",\"ProposalId\":\"GOV-1\",\"ProjectId\":\"{projectId}\",\"User\":\"Bob\",\"Vote\":true}}",
                "bob-pk",
                db);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void GovernanceVotes_ShouldRemainOpenDuringVotingWindowAndCountAuthorVote()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_governance_state_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "VoteProj";
            string proposalId = "GOV-QUORUM";

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProposal\",\"ProposalId\":\"{proposalId}\",\"ProjectId\":\"{projectId}\",\"Title\":\"Adopt release\",\"User\":\"Alice\",\"Timestamp\":\"{DateTime.UtcNow:O}\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            db.SaveBlock(new Block
            {
                Index = 1,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"TargetPublicKey\":\"MIIBSzCB8QYHKoZIzj0CATCB5QIBATAsBgcqhkjOPQEBAiEA/////wAAAAEAAAAAAAAAAAAAAAD///////////////8wRQIhAP////8AAAAA//////////+85vqtpxeehPO5ysL8YyVRAiEA/////wAAAAEAAAAAAAAAAAAAAAD///////////////8EIFrGNdiqOpPns+u9VXaYhrxlHQawzFOw9jvOPD4n0mBLBEEEaxfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZP40Li/hp/m47n60p8D54WK84zV2sxXs7LtkBoN79R9QIhAP////8AAAAA//////////+85vqtpxeehPO5ysL8YyVRAgEBA0IABDL6VFLjFS4CFf8H6Jw0KVD82g5M3M4f3IPQkTyM07gXx5vHl13QnUi4MHEaWmZ2ehZ0x2g7Ak+rqFiX7u9OQfc=\"}}",
                PreviousHash = "prev-role",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            db.SaveBlock(new Block
            {
                Index = 2,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CastVote\",\"ProposalId\":\"{proposalId}\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"Vote\":true}}",
                PreviousHash = "prev",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Status, YesVotes, NoVotes FROM GovernanceProposals WHERE ProposalId = $id";
            cmd.Parameters.AddWithValue("$id", proposalId);
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal("Open", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void GovernanceVotes_ShouldFinalizeApprovedAfterDeadlineWhenMajoritySupports()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_governance_finalize_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "FinalizeProj";
            string proposalId = "GOV-FINALIZE";
            string oldTimestamp = DateTime.UtcNow.AddHours(-2).ToString("O");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 1,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"TargetPublicKey\":\"MIIBSzCB8QYHKoZIzj0CATCB5QIBATAsBgcqhkjOPQEBAiEA/////wAAAAEAAAAAAAAAAAAAAAD///////////////8wRQIhAP////8AAAAA//////////+85vqtpxeehPO5ysL8YyVRAiEA/////wAAAAEAAAAAAAAAAAAAAAD///////////////8EIFrGNdiqOpPns+u9VXaYhrxlHQawzFOw9jvOPD4n0mBLBEEEaxfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZP40Li/hp/m47n60p8D54WK84zV2sxXs7LtkBoN79R9QIhAP////8AAAAA//////////+85vqtpxeehPO5ysL8YyVRAgEBA0IABDL6VFLjFS4CFf8H6Jw0KVD82g5M3M4f3IPQkTyM07gXx5vHl13QnUi4MHEaWmZ2ehZ0x2g7Ak+rqFiX7u9OQfc=\"}}",
                PreviousHash = "prev",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            db.SaveBlock(new Block
            {
                Index = 2,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProposal\",\"ProposalId\":\"{proposalId}\",\"ProjectId\":\"{projectId}\",\"Title\":\"Adopt release\",\"User\":\"Alice\",\"Timestamp\":\"{oldTimestamp}\"}}",
                PreviousHash = "prev2",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO GovernanceVotes (ProposalId, UserName, Vote) VALUES ($proposal, $user1, 1);
                    INSERT INTO GovernanceVotes (ProposalId, UserName, Vote) VALUES ($proposal, $user2, 1);";
                cmd.Parameters.AddWithValue("$proposal", proposalId);
                cmd.Parameters.AddWithValue("$user1", "Bob");
                cmd.Parameters.AddWithValue("$user2", "Alice");
                cmd.ExecuteNonQuery();
            }

            db.RefreshGovernanceStates(projectId);

            using var verifyConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            verifyConn.Open();
            using var verifyCmd = verifyConn.CreateCommand();
            verifyCmd.CommandText = "SELECT Status, YesVotes, NoVotes FROM GovernanceProposals WHERE ProposalId = $id";
            verifyCmd.Parameters.AddWithValue("$id", proposalId);
            using var reader = verifyCmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal("Approved", reader.GetString(0));
            Assert.Equal(2, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void GovernanceVotes_ShouldCloseEarlyWhenAllMembersVoted_AndApproveOnTie()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_governance_early_close_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "EarlyCloseProj";
            string proposalId = "GOV-EARLY";

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 1,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"TargetPublicKey\":\"MIIBSzCB8QYHKoZIzj0CATCB5QIBATAsBgcqhkjOPQEBAiEA/////wAAAAEAAAAAAAAAAAAAAAD///////////////8wRQIhAP////8AAAAA//////////+85vqtpxeehPO5ysL8YyVRAiEA/////wAAAAEAAAAAAAAAAAAAAAD///////////////8EIFrGNdiqOpPns+u9VXaYhrxlHQawzFOw9jvOPD4n0mBLBEEEaxfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZP40Li/hp/m47n60p8D54WK84zV2sxXs7LtkBoN79R9QIhAP////8AAAAA//////////+85vqtpxeehPO5ysL8YyVRAgEBA0IABDL6VFLjFS4CFf8H6Jw0KVD82g5M3M4f3IPQkTyM07gXx5vHl13QnUi4MHEaWmZ2ehZ0x2g7Ak+rqFiX7u9OQfc=\"}}",
                PreviousHash = "prev",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            db.SaveBlock(new Block
            {
                Index = 2,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProposal\",\"ProposalId\":\"{proposalId}\",\"ProjectId\":\"{projectId}\",\"Title\":\"Split vote\",\"User\":\"Alice\",\"Timestamp\":\"{DateTime.UtcNow:O}\"}}",
                PreviousHash = "prev2",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            db.SaveBlock(new Block
            {
                Index = 3,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CastVote\",\"ProposalId\":\"{proposalId}\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"Vote\":true}}",
                PreviousHash = "prev3",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            db.SaveBlock(new Block
            {
                Index = 4,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CastVote\",\"ProposalId\":\"{proposalId}\",\"ProjectId\":\"{projectId}\",\"User\":\"Bob\",\"Vote\":false}}",
                PreviousHash = "prev4",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "bob-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Status, YesVotes, NoVotes FROM GovernanceProposals WHERE ProposalId = $id";
            cmd.Parameters.AddWithValue("$id", proposalId);
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal("Approved", reader.GetString(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(1, reader.GetInt32(2));
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void DatabaseManager_ShouldEncryptSensitiveFieldsWhenPasswordIsConfigured()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_encrypted_{Guid.NewGuid():N}.db");
        const string dbPassword = "test-db-password";
        const string blockPayload = "{\"Type\":\"Create\",\"TaskId\":\"T1\",\"Title\":\"Secret task\"}";

        try
        {
            var db = new DatabaseManager(dbPath, dbPassword);
            string blockHash = Guid.NewGuid().ToString("N");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = blockPayload,
                PreviousHash = "0",
                Hash = blockHash,
                ValidatorPublicKey = "public-key",
                Signature = "signature",
                ChannelId = "System"
            }, "System");

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Password={dbPassword}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Data, ValidatorPublicKey, Signature FROM Blocks WHERE Hash = $hash";
            cmd.Parameters.AddWithValue("$hash", blockHash);
            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            string storedData = reader.GetString(0);
            string storedPublicKey = reader.GetString(1);
            string storedSignature = reader.GetString(2);

            Assert.NotEqual(blockPayload, storedData);
            Assert.StartsWith("enc:v1:", storedData);
            Assert.StartsWith("enc:v1:", storedPublicKey);
            Assert.StartsWith("enc:v1:", storedSignature);

            var chain = db.LoadChain("System");
            Assert.Single(chain);
            Assert.Equal(blockPayload, chain[0].Data);
            Assert.Equal("public-key", chain[0].ValidatorPublicKey);
            Assert.Equal("signature", chain[0].Signature);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void DatabaseManager_ShouldIndexBlockchainDocumentVersions()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_documents_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "DocsProj";
            string documentId = "DOC-1";

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateDocument\",\"ProjectId\":\"{projectId}\",\"DocumentId\":\"{documentId}\",\"Title\":\"Spec\",\"Content\":\"First version\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            db.SaveBlock(new Block
            {
                Index = 1,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"UpdateDocument\",\"ProjectId\":\"{projectId}\",\"DocumentId\":\"{documentId}\",\"Title\":\"Spec\",\"Content\":\"Second version\",\"User\":\"Alice\"}}",
                PreviousHash = "prev",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Version, Content, ContentHash
                FROM DocumentVersions
                WHERE ProjectId = $project AND DocumentId = $document
                ORDER BY Version";
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$document", documentId);

            using var reader = cmd.ExecuteReader();

            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetInt32(0));
            Assert.Equal("First version", reader.GetString(1));
            string firstHash = reader.GetString(2);

            Assert.True(reader.Read());
            Assert.Equal(2, reader.GetInt32(0));
            Assert.Equal("Second version", reader.GetString(1));
            string secondHash = reader.GetString(2);

            Assert.NotEqual(firstHash, secondHash);
            Assert.False(reader.Read());
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void RebuildStateIndexes_ShouldRestoreDocumentHistoryFromBlockchain()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_rebuild_docs_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "RebuildDocsProj";

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateDocument\",\"ProjectId\":\"{projectId}\",\"DocumentId\":\"DOC-REBUILD\",\"Title\":\"Runbook\",\"Content\":\"Recovered content\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = "alice-pk",
                Signature = "sig",
                ChannelId = projectId
            }, projectId);

            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var clear = conn.CreateCommand();
                clear.CommandText = "DELETE FROM DocumentVersions";
                clear.ExecuteNonQuery();
            }

            db.RebuildStateIndexes();

            using var checkConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            checkConn.Open();
            using var cmd = checkConn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM DocumentVersions WHERE ProjectId = $project AND DocumentId = 'DOC-REBUILD'";
            cmd.Parameters.AddWithValue("$project", projectId);

            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void LoadChain_ShouldReadPrototypeScaleDatasetWithinBudget()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_perf_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            string projectId = "PerfProj";

            for (int i = 0; i < 120; i++)
            {
                db.SaveBlock(new Block
                {
                    Index = i,
                    Timestamp = DateTime.UtcNow.AddSeconds(i),
                    Data = $"{{\"Type\":\"Create\",\"TaskId\":\"T-{i}\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"Title\":\"Task {i}\"}}",
                    PreviousHash = i == 0 ? "0" : $"prev-{i}",
                    Hash = Guid.NewGuid().ToString("N"),
                    ValidatorPublicKey = "alice-pk",
                    Signature = "sig",
                    ChannelId = projectId
                }, projectId);
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var chain = db.LoadChain(projectId);
            sw.Stop();

            Assert.Equal(120, chain.Count);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"LoadChain took {sw.Elapsed.TotalMilliseconds} ms");
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void TaskContract_ShouldRejectArtifactRegistrationWithoutProjectId()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_contract_register_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            var contract = new TaskContract();

            bool accepted = contract.Validate(
                "{\"Type\":\"Register\",\"User\":\"Alice\",\"FileName\":\"a.zip\",\"FileHash\":\"Qm123\"}",
                "alice-pk",
                db);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void TaskContract_ShouldRejectTransferInSystemChannel()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_contract_transfer_{Guid.NewGuid():N}.db");
        try
        {
            var db = new DatabaseManager(dbPath, "");
            var contract = new TaskContract();

            bool accepted = contract.Validate(
                "{\"Type\":\"Transfer\",\"ProjectId\":\"System\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"Amount\":5}",
                "alice-pk",
                db);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void AccessControl_ShouldRejectIdentitySpoofingForKnownUser()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_spoof_{Guid.NewGuid():N}.db");
        try
        {
            using var aliceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string alicePublicKey = Convert.ToBase64String(aliceKey.ExportSubjectPublicKeyInfo());
            string attackerPublicKey = Convert.ToBase64String(attackerKey.ExportSubjectPublicKeyInfo());

            var db = new DatabaseManager(dbPath, "");
            string projectId = "SpoofProj";

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = alicePublicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var contract = new AccessControlContract();
            bool accepted = contract.Validate(
                $"{{\"Type\":\"Create\",\"TaskId\":\"T-SPOOF\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"Title\":\"Hijack\"}}",
                attackerPublicKey,
                db);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void AccessControl_ShouldRejectAssignRoleForUnknownProject()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_unknown_project_role_{Guid.NewGuid():N}.db");
        try
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            using var targetKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string targetPublicKey = Convert.ToBase64String(targetKey.ExportSubjectPublicKeyInfo());

            var db = new DatabaseManager(dbPath, "");
            var contract = new AccessControlContract();

            bool accepted = contract.Validate(
                $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"UnknownProj\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"TargetPublicKey\":\"{targetPublicKey}\",\"Role\":\"Worker\"}}",
                publicKey,
                db);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void AccessControl_ShouldRejectAssignRoleWithoutTargetPublicKey()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_assignrole_no_key_{Guid.NewGuid():N}.db");
        try
        {
            using var aliceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string alicePublicKey = Convert.ToBase64String(aliceKey.ExportSubjectPublicKeyInfo());

            var db = new DatabaseManager(dbPath, "");
            string projectId = "RoleProj";

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = alicePublicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var contract = new AccessControlContract();
            bool accepted = contract.Validate(
                $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"Role\":\"Worker\"}}",
                alicePublicKey,
                db);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void DatabaseManager_ShouldBindTargetPublicKeyOnAssignRole()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_bind_target_key_{Guid.NewGuid():N}.db");
        try
        {
            using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var targetKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            string ownerPublicKey = Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo());
            string targetPublicKey = Convert.ToBase64String(targetKey.ExportSubjectPublicKeyInfo());
            string projectId = "BindKeyProj";

            var db = new DatabaseManager(dbPath, "");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = ownerPublicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 1,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"TargetPublicKey\":\"{targetPublicKey}\",\"Role\":\"Developer\"}}",
                PreviousHash = "prev",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = ownerPublicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            string? boundKey = db.GetUserPublicKey("Bob");
            Assert.Equal(targetPublicKey, boundKey);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void AccessControl_ShouldAllowTrustedOracleFeedForProjectMember()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_oracle_feed_{Guid.NewGuid():N}.db");
        string previousTrusted = Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey;
        try
        {
            using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var memberKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var oracleKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            string ownerPublicKey = Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo());
            string memberPublicKey = Convert.ToBase64String(memberKey.ExportSubjectPublicKeyInfo());
            string oraclePublicKey = Convert.ToBase64String(oracleKey.ExportSubjectPublicKeyInfo());
            Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey = oraclePublicKey;

            string projectId = "OracleFeedProj";
            var db = new DatabaseManager(dbPath, "");

            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"CreateProject\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\"}}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = ownerPublicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            db.SaveBlock(new Block
            {
                Index = 1,
                Timestamp = DateTime.UtcNow,
                Data = $"{{\"Type\":\"AssignRole\",\"ProjectId\":\"{projectId}\",\"User\":\"Alice\",\"TargetUser\":\"Bob\",\"TargetPublicKey\":\"{memberPublicKey}\",\"Role\":\"Developer\"}}",
                PreviousHash = "prev",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = ownerPublicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var contract = new AccessControlContract();
            bool accepted = contract.Validate(
                $"{{\"Type\":\"CodeCommit\",\"Source\":\"GitEvent\",\"Provider\":\"GitHubPush\",\"ProjectId\":\"{projectId}\",\"User\":\"Bob\",\"CommitHash\":\"abcdef1\",\"Repository\":\"repo\"}}",
                oraclePublicKey,
                db);

            Assert.True(accepted);
        }
        finally
        {
            Blockchain.Core.Constants.NetworkParameters.TrustedOraclePublicKey = previousTrusted;
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void BlockchainGrpcService_ShouldAcceptSignedReadRequestForBoundUser()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_read_auth_{Guid.NewGuid():N}.db");
        try
        {
            using var userKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(userKey.ExportSubjectPublicKeyInfo());

            var db = new DatabaseManager(dbPath, "");
            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = "{\"Type\":\"CreateProject\",\"ProjectId\":\"ReadAuthProj\",\"User\":\"Alice\"}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = publicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var service = CreateGrpcService(dbPath, nodeAdminToken: "admin-secret");
            string scope = "PROJECT:ReadAuthProj:TASKS";
            string timestamp = DateTime.UtcNow.ToString("O");
            string nonce = Guid.NewGuid().ToString("N");
            string signature = SignRead(userKey, scope, "Alice", publicKey, timestamp, nonce);

            bool accepted = InvokeReadAuthorization(service, db, scope, "Alice", publicKey, signature, timestamp, nonce);

            Assert.True(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void BlockchainGrpcService_ShouldRejectSignedReadRequestWithMismatchedBoundKey()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_read_auth_reject_{Guid.NewGuid():N}.db");
        try
        {
            using var ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string ownerPublicKey = Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo());
            string attackerPublicKey = Convert.ToBase64String(attackerKey.ExportSubjectPublicKeyInfo());

            var db = new DatabaseManager(dbPath, "");
            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = "{\"Type\":\"CreateProject\",\"ProjectId\":\"ReadAuthRejectProj\",\"User\":\"Alice\"}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = ownerPublicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var service = CreateGrpcService(dbPath, nodeAdminToken: "admin-secret");
            string scope = "PROJECT:ReadAuthRejectProj:TASKS";
            string timestamp = DateTime.UtcNow.ToString("O");
            string nonce = Guid.NewGuid().ToString("N");
            string attackerSignature = SignRead(attackerKey, scope, "Alice", attackerPublicKey, timestamp, nonce);

            bool accepted = InvokeReadAuthorization(service, db, scope, "Alice", attackerPublicKey, attackerSignature, timestamp, nonce);

            Assert.False(accepted);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    [Fact]
    public void BlockchainGrpcService_ShouldRejectReplayedSignedReadNonce()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"nexus_read_auth_replay_{Guid.NewGuid():N}.db");
        try
        {
            using var userKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string publicKey = Convert.ToBase64String(userKey.ExportSubjectPublicKeyInfo());

            var db = new DatabaseManager(dbPath, "");
            db.SaveBlock(new Block
            {
                Index = 0,
                Timestamp = DateTime.UtcNow,
                Data = "{\"Type\":\"CreateProject\",\"ProjectId\":\"ReadAuthReplayProj\",\"User\":\"Alice\"}",
                PreviousHash = "0",
                Hash = Guid.NewGuid().ToString("N"),
                ValidatorPublicKey = publicKey,
                Signature = "sig",
                ChannelId = "System"
            }, "System");

            var service = CreateGrpcService(dbPath, nodeAdminToken: "admin-secret");
            string scope = "PROJECT:ReadAuthReplayProj:TASKS";
            string timestamp = DateTime.UtcNow.ToString("O");
            string nonce = Guid.NewGuid().ToString("N");
            string signature = SignRead(userKey, scope, "Alice", publicKey, timestamp, nonce);

            bool firstAttempt = InvokeReadAuthorization(service, db, scope, "Alice", publicKey, signature, timestamp, nonce);
            bool replayAttempt = InvokeReadAuthorization(service, db, scope, "Alice", publicKey, signature, timestamp, nonce);

            Assert.True(firstAttempt);
            Assert.False(replayAttempt);
        }
        finally
        {
            TryDelete(dbPath);
            TryDelete(dbPath + "-wal");
            TryDelete(dbPath + "-shm");
        }
    }

    private static BlockchainGrpcService CreateGrpcService(string dbPath, string nodeAdminToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultNodeDb"] = dbPath,
                ["NodeDbPassword"] = "test-db-password",
                ["NodeAdminToken"] = nodeAdminToken,
                ["Node:Role"] = "Local"
            })
            .Build();

        var database = new DatabaseManager(dbPath, "");
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSignalR();
        services.AddNexusNodeServices(database);

        var provider = services.BuildServiceProvider();
        return ActivatorUtilities.CreateInstance<BlockchainGrpcService>(provider);
    }

    private static BlockchainManager CreateBlockchainManager(string dbPath)
    {
        return new BlockchainManager(new DatabaseManager(dbPath, ""));
    }

    private static bool InvokeReadAuthorization(
        BlockchainGrpcService service,
        DatabaseManager db,
        string scope,
        string userName,
        string publicKey,
        string signature,
        string timestamp,
        string nonce)
    {
        var method = typeof(BlockchainGrpcService).GetMethod("IsAuthorizedReadRequest", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method!.Invoke(service, new object[] { db, scope, userName, publicKey, signature, timestamp, nonce })!;
    }

    private static string SignRead(ECDsa key, string scope, string userName, string publicKey, string timestamp, string nonce)
    {
        string signable = $"READ:{scope}:{userName}:{publicKey}:{timestamp}:{nonce}";
        byte[] signature = key.SignData(
            System.Text.Encoding.UTF8.GetBytes(signable),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // test cleanup best-effort
        }
    }
}
