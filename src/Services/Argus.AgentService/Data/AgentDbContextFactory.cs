namespace Argus.AgentService.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

public sealed class AgentDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AgentDbContext>();
        optionsBuilder.UseNpgsql("Host=127.0.0.1;Port=32775;Database=argusdb;Username=postgres;Password=cHUSwT9h!SFtwg6654{({8");
        return new AgentDbContext(optionsBuilder.Options);
    }
}
