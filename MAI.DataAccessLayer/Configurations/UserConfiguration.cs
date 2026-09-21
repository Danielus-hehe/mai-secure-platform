using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MAI.Domain.Entities;

namespace MAI.DataAccessLayer.Configurations
{
    /// <summary>
    /// Configurarea utilizatorilor.
    ///
    /// Deliberat minimală: doar indexuri. Adăugarea de HasMaxLength aici ar
    /// genera ALTER COLUMN pe coloane care conțin deja date, cu risc de
    /// trunchiere - o schimbare separată, făcută conștient, nu un efect
    /// secundar al adăugării unui index.
    /// </summary>
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            // Unicitatea contează mai mult decât viteza: fără ea, două conturi
            // pot ajunge cu același nume, iar FirstOrDefaultAsync îl alege pe
            // unul nedeterminist. Rezultatul e un utilizator care se
            // autentifică uneori pe contul altcuiva.
            builder.HasIndex(u => u.Username)
                .IsUnique()
                .HasDatabaseName("UX_Users_Username");

            // Tokenul de invitație (hash SHA-256) e căutat la fiecare deschidere a
            // linkului de activare. Unic, ca un hash să indice un singur cont;
            // parțial, ca toate conturile fără invitație (NULL) să nu intre în
            // index. Declarat aici ca EF să-l cunoască: altfel următorul
            // `migrations add` l-ar vedea doar în bază și ar genera un DropIndex.
            builder.HasIndex(u => u.InvitationToken)
                .IsUnique()
                .HasFilter("\"InvitationToken\" IS NOT NULL")
                .HasDatabaseName("IX_Users_InvitationToken");

            // Tokenul de resetare a parolei - același tipar ca invitația.
            builder.HasIndex(u => u.PasswordResetToken)
                .IsUnique()
                .HasFilter("\"PasswordResetToken\" IS NOT NULL")
                .HasDatabaseName("IX_Users_PasswordResetToken");

            // ── Cont de domeniu ──────────────────────────────────────────
            // objectGUID din AD, unic: două conturi locale nu au voie să indice
            // același cont de domeniu. Fără index, o legare greșită ar duce la
            // doi utilizatori care se autentifică amândoi cu aceeași parolă de
            // domeniu, pe conturi diferite, cu chei E2EE diferite.
            builder.Property(u => u.DirectoryObjectId).HasMaxLength(64);
            builder.Property(u => u.DirectoryDn).HasMaxLength(512);

            builder.HasIndex(u => u.DirectoryObjectId)
                .IsUnique()
                .HasFilter("\"DirectoryObjectId\" IS NOT NULL")
                .HasDatabaseName("UX_Users_DirectoryObjectId");

            // Încadrarea în structură. Restrict: o subdiviziune cu membri nu se
            // poate șterge; se mută întâi oamenii sau se dezactivează.
            builder.HasOne(u => u.OrgUnit)
                .WithMany()
                .HasForeignKey(u => u.OrgUnitId)
                .OnDelete(DeleteBehavior.Restrict);

            builder.HasIndex(u => u.OrgUnitId)
                .HasDatabaseName("IX_Users_OrgUnitId");

            // Indexurile care contează efectiv pentru unicitate NU apar aici, ci
            // în migrarea CaseInsensitiveUserIndexes, ca SQL:
            //
            //   UX_Users_Username_Lower  UNIQUE (lower("Username"))
            //   UX_Users_Email_Lower     UNIQUE (lower("Email")) WHERE "Email" <> ''
            //
            // EF Core nu poate descrie indexuri pe expresii. Primul face ca
            // „Ion.Popescu” și „ion.popescu” să nu poată fi două conturi diferite
            // și e folosit de login (lower("Username") = ...). Al doilea permite
            // mai multe conturi fără email: vechiul UX_Users_Email, unic pe
            // valoarea exactă, trata "" ca pe o adresă, deci al doilea cont fără
            // email pica la salvare cu eroare 500.
        }
    }
}