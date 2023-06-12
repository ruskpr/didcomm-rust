using System.Text.Json.Serialization;
using uniffi.didcomm;

namespace ConsoleApp;

class Program
{
    static void Main(string[] args)
    {
        Message m = new Message(
            "1234567890",
            "application/didcomm-plain+json",
            "http://example.com/protocols/lets_do_lunch/1.0/proposal",
            "{\"messagespecificattribute\": \"and its value\"}",
            "did:person:alice",
            new List<string> { "did:person:bob" },
            null,
            null,
            new Dictionary<string, string>(),
            1516269022,
            1516385931,
            null,
            null
        );

        PackSignedUnencrypted(m);
    }

    private static void PackSignedUnencrypted(Message m)
    {
        var didResolver = new ExampleDidResolver(new List<DidDoc>() { Constants.ALICE_DID_DOC, Constants.BOB_DID_DOC });
        var secretsResolver = new ExampleSecretsResolver(Constants.ALICE_SECRETS);

        var res = new DidMessageResult();

        _ = new DidComm(didResolver, secretsResolver).PackSigned(m, Constants.ALICE_DID, res);
    }
}

public class DidMessageResult : OnPackSignedResult
{
    public void Error(ErrorKind err, string msg)
    {
        throw new Exception($"{err}: {msg}");
    }

    public void Success(string result, PackSignedMetadata metadata)
    {
        Console.WriteLine(result + $"\nMETADATA: {metadata}");
    }
}