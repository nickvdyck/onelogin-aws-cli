namespace OneloginAwsCli.Api
{
    public class OneLoginCredentials
    {
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }

        public OneLoginCredentials(string clientId, string clientSecret) =>
            (ClientId, ClientSecret) = (clientId, clientSecret);
    }
}
