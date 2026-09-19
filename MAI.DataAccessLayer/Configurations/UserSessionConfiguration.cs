using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MAI.Domain.Entities;

namespace MAI.DataAccessLayer.Configurations
{
    /// <summary>
    /// Configurarea sesiunilor. Tabela e citită la fiecare /refresh, deci
    /// indexul pe hash-ul tokenului este pe calea critică a autentificării.
    /// </summary>
    public class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
    {
        public void Configure(EntityTypeBuilder<UserSession> builder)
        {
            builder.HasKey(s => s.Id);

            builder.Property(s => s.RefreshTokenHash)
                .IsRequired()
                .HasMaxLength(64);          // SHA-256 în hex

            builder.Property(s => s.UserAgent).HasMaxLength(256);
            builder.Property(s => s.IpAddress).HasMaxLength(64);
            builder.Property(s => s.RevokedReason).HasMaxLength(128);

            // Calculată din RevokedAt și ExpiresAt - EF ar căuta altfel o coloană.
            builder.Ignore(s => s.IsActive);

            // Ștergerea unui utilizator îi ia sesiunile cu ea. Aici cascada e
            // corectă: o sesiune fără utilizator nu înseamnă nimic și nu poate fi
            // revocată de nimeni.
            builder.HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Căutarea de la /refresh: un singur rând, pe index unic.
            //
            // Unicitatea e o protecție reală, nu doar performanță: două sesiuni
            // cu același hash ar însemna că generatorul de tokenuri a repetat o
            // valoare, iar refresh-ul ar deveni nedeterminist. Mai bine eșuează
            // inserarea zgomotos.
            builder.HasIndex(s => s.RefreshTokenHash)
                .IsUnique()
                .HasDatabaseName("IX_UserSessions_RefreshTokenHash");

            // Listarea sesiunilor proprii, cele active întâi.
            builder.HasIndex(s => new { s.UserId, s.RevokedAt })
                .HasDatabaseName("IX_UserSessions_UserId_RevokedAt");

            // Curățarea periodică a sesiunilor expirate.
            builder.HasIndex(s => s.ExpiresAt)
                .HasDatabaseName("IX_UserSessions_ExpiresAt");
        }
    }
}
