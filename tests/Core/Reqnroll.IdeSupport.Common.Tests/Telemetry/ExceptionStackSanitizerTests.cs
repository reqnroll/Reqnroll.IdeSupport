using System.Runtime.CompilerServices;
using Reqnroll.IdeSupport.Common.Telemetry;

namespace Reqnroll.IdeSupport.Common.Tests.Telemetry;

public class ExceptionStackSanitizerTests
{
    private const string Prefix = "Reqnroll.IdeSupport.Common.Tests.Telemetry.ExceptionStackSanitizerTests";

    [Fact]
    public void Returns_null_for_an_exception_that_was_never_thrown()
    {
        ExceptionStackSanitizer.Sanitize(new InvalidOperationException("x")).Should().BeNull();
    }

    [Fact]
    public void Names_frames_as_Namespace_Type_Method_without_paths_lines_or_parameters()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => ThrowWithParameters("secret-scenario-name", 42)))!;

        var frames = result.Split(ExceptionStackSanitizer.FrameSeparator);
        frames[0].Should().Be($"{Prefix}.ThrowWithParameters");
        result.Should().NotContain("secret-scenario-name")
            .And.NotContain("(").And.NotContain(")")
            .And.NotContain(".cs").And.NotContain(":line")
            .And.NotContain("\\").And.NotContain("/");
    }

    [Fact]
    public void Normalises_lambdas_to_the_declaring_member()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => throw new InvalidOperationException("boom")))!;

        result.Split(ExceptionStackSanitizer.FrameSeparator)[0]
            .Should().Be($"{Prefix}.{nameof(Normalises_lambdas_to_the_declaring_member)}{{lambda}}");
        result.Should().NotContain("<").And.NotContain("b__").And.NotContain("DisplayClass");
    }

    [Fact]
    public async Task Folds_async_state_machines_into_the_declaring_method()
    {
        var ex = await ThrowAsync();

        ExceptionStackSanitizer.Sanitize(ex)!.Split(ExceptionStackSanitizer.FrameSeparator)[0]
            .Should().Be($"{Prefix}.{nameof(ThrowAsync)}");
    }

    [Fact]
    public void Drops_generic_arguments_from_types_and_methods()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => new GenericHolder<UserSecretType>().Go<UserSecretType>()))!;

        result.Split(ExceptionStackSanitizer.FrameSeparator)[0].Should().Be($"{Prefix}.GenericHolder.Go");
        result.Should().NotContain("UserSecretType").And.NotContain("`").And.NotContain("[");
    }

    [Fact]
    public void Collapses_frames_from_other_namespaces_into_one_placeholder_and_never_names_them()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => UserCode.Steps.Outer()))!;

        result.Should().NotContain("UserCode").And.NotContain("Outer").And.NotContain("Inner");
        var frames = result.Split(ExceptionStackSanitizer.FrameSeparator);
        frames.Should().Contain(ExceptionStackSanitizer.ExternalPlaceholder);
        // two consecutive external frames (Inner, Outer) must be one placeholder
        for (var i = 1; i < frames.Length; i++)
            (frames[i] == ExceptionStackSanitizer.ExternalPlaceholder && frames[i - 1] == ExceptionStackSanitizer.ExternalPlaceholder)
                .Should().BeFalse();
    }

    [Fact]
    public void Does_not_treat_a_lookalike_namespace_as_the_product()
    {
        ExceptionStackSanitizer.Sanitize(Thrown(() => Reqnroll.IdeSupportLookalike.Evil.Throw()))!
            .Should().NotContain("Lookalike");
    }

    [Fact]
    public void Returns_null_when_no_frame_belongs_to_the_product()
    {
        // Thrown on a pool thread inside foreign code, so no product frame is on its stack.
        var foreign = UserCode.Steps.ThrowFromForeignTask();
        ExceptionStackSanitizer.Sanitize(foreign).Should().BeNull();
    }

    [Fact]
    public void Bounds_the_number_of_frames()
    {
        var ex = Thrown(() => Recurse(50));

        var result = ExceptionStackSanitizer.Sanitize(ex)!;
        result.Split(ExceptionStackSanitizer.FrameSeparator).Should().HaveCount(ExceptionStackSanitizer.DefaultMaxFrames);

        ExceptionStackSanitizer.Sanitize(ex, maxFrames: 3)!.Split(ExceptionStackSanitizer.FrameSeparator).Should().HaveCount(3);
    }

    [Fact]
    public void Caps_total_length()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => Recurse(50)), maxFrames: 1000)!;

        result.Length.Should().BeLessThanOrEqualTo(ExceptionStackSanitizer.MaxLength);
    }

    [Fact]
    public void Never_reads_the_exception_message()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => throw new InvalidOperationException(@"C:\Users\alice\Feature 'Login'.feature")))!;

        result.Should().NotContain("alice").And.NotContain("Login");
    }

    private static Exception Thrown(Action action)
    {
        try { action(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("did not throw");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWithParameters(string scenarioName, int n) =>
        throw new InvalidOperationException(scenarioName + n);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Recurse(int depth)
    {
        if (depth == 0) throw new InvalidOperationException("deep");
        Recurse(depth - 1);
    }

    private static async Task<Exception> ThrowAsync()
    {
        try
        {
            await Task.Yield();
            throw new InvalidOperationException("async");
        }
        catch (Exception ex) { return ex; }
    }

    private sealed class UserSecretType { }

    private sealed class GenericHolder<T>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Go<TArg>() => throw new InvalidOperationException("generic");
    }
}
