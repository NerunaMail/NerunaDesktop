using System.Security.Cryptography;
using System.Text;
using Neruna.Vault;

namespace Neruna.Shared.Tests;

public sealed class AccountTransferTests
{
    [Fact]
    public void The_pin_opens_the_code_and_nothing_else_does()
    {
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"Host\":\"imap.example.com\",\"Secret\":\"geheim\"}", 10)));
        var pin = AccountTransfer.NewPin();

        var code = AccountTransfer.Seal(payload, pin);

        Assert.Equal(6, pin.Length);
        Assert.True(AccountTransfer.IsTransfer(code));
        Assert.DoesNotContain("geheim", code, StringComparison.Ordinal);
        Assert.True(code.Length < 300, $"{code.Length} characters"); // compressed: fits a QR code easily
        Assert.Equal(payload, AccountTransfer.Open(code, pin));
        Assert.Equal(payload, AccountTransfer.Open(code, $" {pin} "));
        Assert.ThrowsAny<CryptographicException>(() => AccountTransfer.Open(code, pin == "000000" ? "000001" : "000000"));
    }

    [Fact]
    public void A_changed_or_foreign_code_is_refused()
    {
        var code = AccountTransfer.Seal([1, 2, 3], "123456");
        var changed = code[..^4] + (code[^4] == 'A' ? 'B' : 'A') + code[^3..];

        Assert.ThrowsAny<CryptographicException>(() => AccountTransfer.Open(changed, "123456"));
        Assert.ThrowsAny<CryptographicException>(() => AccountTransfer.Open("neruna-pair:1?server=x", "123456"));
        Assert.ThrowsAny<CryptographicException>(() => AccountTransfer.Open(AccountTransfer.Prefix + "%%", "123456"));
    }
}
