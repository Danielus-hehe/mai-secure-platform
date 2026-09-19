using MAI.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MAI.DataAccessLayer.Configurations
{
    public class OrgLevelConfiguration : IEntityTypeConfiguration<OrgLevel>
    {
        public void Configure(EntityTypeBuilder<OrgLevel> builder)
        {
            builder.ToTable("OrgLevels");
            builder.HasKey(l => l.Rank);

            // Rangul e ales de aplicație (OrgLevelRules), nu generat de bază.
            builder.Property(l => l.Rank).ValueGeneratedNever();
            builder.Property(l => l.Name).IsRequired().HasMaxLength(60);
        }
    }
}
