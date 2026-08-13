using DireControl.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace DireControl.Data;

public class DireControlContext(DbContextOptions<DireControlContext> options) : DbContext(options)
{
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<Packet> Packets => Set<Packet>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Geofence> Geofences => Set<Geofence>();
    public DbSet<ProximityRule> ProximityRules => Set<ProximityRule>();
    public DbSet<StationStatistic> StationStatistics => Set<StationStatistic>();
    public DbSet<Radio> Radios => Set<Radio>();
    public DbSet<OwnBeacon> OwnBeacons => Set<OwnBeacon>();
    public DbSet<DigiConfirmation> DigiConfirmations => Set<DigiConfirmation>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<DigipeaterStatistic> DigipeaterStatistics => Set<DigipeaterStatistic>();
    public DbSet<CoverageGridStatistic> CoverageGridStatistics => Set<CoverageGridStatistic>();
    public DbSet<LogLevelOverride> LogLevelOverrides => Set<LogLevelOverride>();
    public DbSet<PmsMessage> PmsMessages => Set<PmsMessage>();
    public DbSet<TerminalSessionRecord> TerminalSessionRecords => Set<TerminalSessionRecord>();
    public DbSet<TerminalTranscriptChunk> TerminalTranscriptChunks => Set<TerminalTranscriptChunk>();
    public DbSet<TerminalPreset> TerminalPresets => Set<TerminalPreset>();
    public DbSet<TerminalMacro> TerminalMacros => Set<TerminalMacro>();
    public DbSet<LightningStrikeRecord> LightningStrikes => Set<LightningStrikeRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DireControlContext).Assembly);
    }
}
