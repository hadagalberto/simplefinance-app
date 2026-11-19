using SimpleFinance.Infrastructure.Security;

namespace SimpleFinance.UnitTests.Security;

public class Argon2idPasswordHasherTests
{
    private readonly Argon2idPasswordHasher _sut = new();

    [Fact]
    public void Verify_WithCorrectPassword_ReturnsTrue()
    {
        const string password = "correct horse battery staple";
        var hash = _sut.Hash(password);

        var result = _sut.Verify(password, hash);

        Assert.True(result);
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        const string password = "correct horse battery staple";
        var hash = _sut.Hash(password);

        var result = _sut.Verify("wrong password", hash);

        Assert.False(result);
    }

    [Fact]
    public void Hash_CalledTwiceWithSamePassword_ProducesDifferentHashes()
    {
        const string password = "correct horse battery staple";

        var hash1 = _sut.Hash(password);
        var hash2 = _sut.Hash(password);

        Assert.NotEqual(hash1, hash2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-valid-hash")]
    [InlineData("$argon2id$garbage")]
    public void Verify_WithMalformedOrEmptyHash_ReturnsFalseInsteadOfThrowing(string malformedHash)
    {
        var result = _sut.Verify("any password", malformedHash);

        Assert.False(result);
    }
}
