using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MAI.Domain.Entities;

namespace MAI.DataAccessLayer.Configurations
{
    public class FileTransferConfiguration : IEntityTypeConfiguration<FileTransfer>
    {
        public void Configure(EntityTypeBuilder<FileTransfer> builder)
        {
            builder.HasOne(f => f.Sender)
                .WithMany()
                .HasForeignKey(f => f.SenderId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasOne(f => f.Recipient)
                .WithMany()
                .HasForeignKey(f => f.RecipientId)
                .OnDelete(DeleteBehavior.Restrict);
        }
    }
}