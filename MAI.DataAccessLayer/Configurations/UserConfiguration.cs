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
            // Unicitatea contează mai mult decât viteza: fără ea, două conturi
            // pot ajunge cu același nume, iar FirstOrDefaultAsync îl alege pe
            // unul nedeterminist. Rezultatul e un utilizator care se
            // autentifică uneori pe contul altcuiva.
            builder.HasIndex(u => u.Username)
                .IsUnique()
                .HasDatabaseName("UX_Users_Username");

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