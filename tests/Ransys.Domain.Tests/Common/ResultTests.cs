using Ransys.Domain.Common;

namespace Ransys.Domain.Tests.Common;

public sealed class ResultTests
{
    private static readonly RansysError Error = RansysError.Financial("INSUFFICIENT_BALANCE", "Not enough balance.");

    [Fact]
    public void Success_exposes_value_and_hides_error()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Failure_exposes_error_and_hides_value()
    {
        Result<int> result = Error;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.Financial, result.Error.Category);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Default_result_is_not_a_silent_success()
    {
        var result = default(Result<int>);

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void Non_generic_result()
    {
        Assert.True(Result.Success().IsSuccess);

        Result failure = Error;
        Assert.Equal("INSUFFICIENT_BALANCE", failure.Error.Code);
    }
}
