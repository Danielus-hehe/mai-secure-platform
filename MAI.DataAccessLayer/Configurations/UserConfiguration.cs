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
    /// trunchiere — o schimbare separată, făcută conștient, nu un efect
    /// secundar al adăugării unui index.
    /// </summary>
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            // Login-ul caută WHERE "Username" = ... la fiecare autentificare.
            // Fără index, scan complet pe tabelă.
            //
            // Unicitatea contează mai mult decât viteza: fără ea, două conturi
            // pot ajunge cu același nume, iar FirstOrDefaultAsync îl alege pe
            // unul nedeterminist. Rezultatul e un utilizator care se
            // autentifică uneori pe contul altcuiva.
            builder.HasIndex(u => u.Username)
                .IsUnique()
                .HasDatabaseName("UX_Users_Username");

            builder.HasIndex(u => u.Email)
                .IsUnique()
                .HasDatabaseName("UX_Users_Email");
        }
    }
}