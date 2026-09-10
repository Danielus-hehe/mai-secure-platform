namespace MAI.Api.Models
{
    // Modele de binding pentru request-urile multipart/form-data.
    // Swashbuckle nu poate genera schema când [FromForm] e pus pe parametri
    // individuali alături de IFormFile — trebuie un singur obiect [FromForm].
    // Numele proprietăților = numele câmpurilor trimise de frontend (FormData).

    public class CreateDocumentRequest
    {
        public string    Title    { get; set; } = string.Empty;
        public string?   Number   { get; set; }
        public string    Category { get; set; } = string.Empty;
        public string?   Keywords { get; set; }
        public IFormFile? File    { get; set; }
    }

    public class AddDocumentVersionRequest
    {
        public IFormFile? File        { get; set; }
        public string?    ChangeNotes { get; set; }
    }

}
