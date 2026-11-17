namespace CloneDeploy_Server.Backend;

// Infrastructure contract: failures propagate to the calling operation.
// Never substitute an empty secret or log plaintext/protected values.
public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}