using Xunit;

namespace MAI.IntegrationTests;

/// <summary>
/// Colectie partajata: toate clasele de test din aceasta colectie impart
/// aceeasi instanta SgdmWebFactory (deci acelasi container PostgreSQL).
///
/// Un singur container pentru toate clasele reduce timpul de pornire de la
/// ~N*5s la ~5s. Pretul: testele isi impart baza de date, deci fiecare test
/// isi creeaza utilizatori cu prefix unic, ca sa nu se suprapuna.
/// </summary>
[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<SgdmWebFactory>
{
    // Clasa goala: doar atributul conteaza.
}
