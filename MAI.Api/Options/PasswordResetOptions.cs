namespace MAI.Api.Options
{
    /// <summary>
    /// Resetarea parolei prin email (secțiunea „PasswordReset”; din .env:
    /// PASSWORD_RESET_TOKEN_MINUTES).
    /// </summary>
    public class PasswordResetOptions
    {
        /// <summary>
        /// Cât e valabil linkul. Mai scurt decât invitația (72 h): un link de
        /// resetare dă controlul unui cont existent, cu tot ce a primit deja.
        /// </summary>
        public int TokenMinutes { get; set; } = 120;

        public TimeSpan TokenLifetime => TimeSpan.FromMinutes(TokenMinutes);

        public void Validate()
        {
            if (TokenMinutes is < 5 or > 1440)
                throw new InvalidOperationException("PasswordReset:TokenMinutes trebuie să fie între 5 și 1440 (24 de ore).");
        }
    }
}
