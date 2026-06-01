using Blockchain.Core;
using Blockchain.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Blockchain.Infrastructure.Services;

public sealed class ProjectReadConnectionFactory
{
    public string ConnectionString { get; }

    public ProjectReadConnectionFactory(DatabaseManager database, IConfiguration configuration)
    {
        string dbPassword = configuration["NodeDbPassword"]
            ?? throw new InvalidOperationException(
                "NodeDbPassword is not configured. Set it via .NET user-secrets or environment variables.");

        ConnectionString = $"Data Source={database.DbFileName};Password={dbPassword}";
    }
}
