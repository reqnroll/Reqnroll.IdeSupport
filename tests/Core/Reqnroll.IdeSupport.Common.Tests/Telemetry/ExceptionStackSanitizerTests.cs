using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
        frames[0].WithoutLine().Should().Be($"{Prefix}.ThrowWithParameters");
        result.Should().NotContain("secret-scenario-name")
            .And.NotContain("(").And.NotContain(")")
            .And.NotContain(".cs").And.NotContain(":line")
            .And.NotContain("\\").And.NotContain("/");
    }

    [Fact]
    public void Normalises_lambdas_to_the_declaring_member()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => throw new InvalidOperationException("boom")))!;

        result.Split(ExceptionStackSanitizer.FrameSeparator)[0].WithoutLine()
            .Should().Be($"{Prefix}.{nameof(Normalises_lambdas_to_the_declaring_member)}{{lambda}}");
        result.Should().NotContain("<").And.NotContain("b__").And.NotContain("DisplayClass");
    }

    [Fact]
    public async Task Folds_async_state_machines_into_the_declaring_method()
    {
        var ex = await ThrowAsync();

        ExceptionStackSanitizer.Sanitize(ex)!.Split(ExceptionStackSanitizer.FrameSeparator)[0].WithoutLine()
            .Should().Be($"{Prefix}.{nameof(ThrowAsync)}");
    }

    [Fact]
    public void Drops_generic_arguments_from_types_and_methods()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => new GenericHolder<UserSecretType>().Go<UserSecretType>()))!;

        result.Split(ExceptionStackSanitizer.FrameSeparator)[0].WithoutLine().Should().Be($"{Prefix}.GenericHolder.Go");
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

    private static string First(string sanitized) => sanitized.Split(ExceptionStackSanitizer.FrameSeparator)[0].WithoutLine();

    [Fact]
    public void IsProductType_requires_both_namespace_and_assembly_in_the_product_tree()
    {
        ExceptionStackSanitizer.IsProductType(typeof(ExceptionStackSanitizerTests)).Should().BeTrue();
        ExceptionStackSanitizer.IsProductType(typeof(string)).Should().BeFalse();
        ExceptionStackSanitizer.IsProductType(null!).Should().BeFalse();
        ExceptionStackSanitizer.IsProductType(typeof(Reqnroll.IdeSupportLookalike.Evil)).Should().BeFalse();
        ExceptionStackSanitizer.IsProductType(ImpostorType()).Should().BeFalse();
    }

    [Fact]
    public void Does_not_name_an_exact_namespace_impostor_declared_in_a_foreign_assembly()
    {
        var run = ImpostorType().GetMethod("Run")!;
        var action = (Action<Action>)Delegate.CreateDelegate(typeof(Action<Action>), run);

        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => action(() => throw new InvalidOperationException("x"))))!;

        result.Should().NotContain("Impostor").And.NotContain("ForeignDyn");
        result.Split(ExceptionStackSanitizer.FrameSeparator).Should().Contain(ExceptionStackSanitizer.ExternalPlaceholder);
    }

    [Fact]
    public void Explicit_interface_implementation_never_emits_the_interface_or_its_generic_arguments()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => ((IHandler<UserSecretType>)new ExplicitImpl()).Handle()))!;

        First(result).Should().Be($"{Prefix}.ExplicitImpl.Handle");
        result.Should().NotContain("UserSecretType").And.NotContain("IHandler");
    }

    [Fact]
    public void Local_function_inside_a_lambda_is_reported_as_its_enclosing_member()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(LocalInLambda))!;

        First(result).Should().Be($"{Prefix}.LocalInLambda{{local}}");
        result.Should().NotContain("<").And.NotContain("|").And.NotContain(">");
    }

    [Fact]
    public void Iterator_methods_are_reported_by_their_declaring_member()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => Iterator().ToList()))!;

        First(result).Should().Be($"{Prefix}.Iterator");
    }

    [Fact]
    public void Property_accessors_are_reported_by_accessor_name()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => _ = ThrowingProperty))!;

        First(result).Should().Be($"{Prefix}.get_ThrowingProperty");
    }

    [Fact]
    public void Static_constructors_are_reported_as_cctor()
    {
        var typeInit = Thrown(() => ThrowingStaticCtor.Touch());

        ExceptionStackSanitizer.Sanitize(typeInit.InnerException!)!
            .Should().StartWith($"{Prefix}.ThrowingStaticCtor.{{cctor}}");
    }

    [Fact]
    public void Nested_type_inside_a_generic_type_drops_the_arity_and_arguments()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => new OuterGeneric<UserSecretType>.Inner().Go()))!;

        First(result).Should().Be($"{Prefix}.OuterGeneric.Inner.Go");
        result.Should().NotContain("UserSecretType").And.NotContain("`");
    }

    [Fact]
    public void Never_emits_user_named_exception_types_or_chained_exceptions()
    {
        var ex = Thrown(() => throw new AcmeCustomerException("c", new InvalidOperationException("inner-secret", new FormatException("deepest-secret"))));

        var result = ExceptionStackSanitizer.Sanitize(ex)!;

        result.Should().NotContain("Acme").And.NotContain("Customer").And.NotContain("secret");
    }

    [Fact]
    public void A_very_deep_recursion_is_truncated_to_the_frame_cap()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => Recurse(3000)))!;

        var frames = result.Split(ExceptionStackSanitizer.FrameSeparator);
        frames.Should().HaveCount(ExceptionStackSanitizer.DefaultMaxFrames);
        frames[0].WithoutLine().Should().Be($"{Prefix}.Recurse");
        result.Length.Should().BeLessThanOrEqualTo(ExceptionStackSanitizer.MaxLength);
    }

    [Fact]
    public void Capture_Source_ignores_the_lookalike_namespace_and_names_the_first_product_class()
    {
        var captured = ExceptionStackSanitizer.Capture(Thrown(() => Reqnroll.IdeSupportLookalike.Evil.Throw()))!;

        captured.Source.Should().Be(nameof(ExceptionStackSanitizerTests));
    }

    [Fact]
    public void Appends_the_line_number_to_product_frames_only()
    {
        var ex = Thrown(() => ThrowAtKnownLine(out _));
        var line = ((KnownLine)ex).Line;

        var frames = ExceptionStackSanitizer.Sanitize(ex)!.Split(ExceptionStackSanitizer.FrameSeparator);

        frames[0].Should().Be($"{Prefix}.ThrowAtKnownLine:{line}");
        frames.Where(f => f != ExceptionStackSanitizer.ExternalPlaceholder)
            .Should().OnlyContain(f => Regex.IsMatch(f, @":\d+$"));
    }

    [Fact]
    public void Line_numbers_add_no_path_column_or_file_name_to_the_output()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => ThrowWithParameters("s", 1)))!;

        foreach (var frame in result.Split(ExceptionStackSanitizer.FrameSeparator).Where(f => f != ExceptionStackSanitizer.ExternalPlaceholder))
            Regex.IsMatch(frame, @"^[\w.{}]+:\d+$").Should().BeTrue(frame);
        result.Should().NotContain(".cs").And.NotContain("\\").And.NotContain("/").And.NotContain(" ");
    }

    [Fact]
    public void Never_emits_a_zero_line_for_frames_without_debug_info()
    {
        var result = ExceptionStackSanitizer.Sanitize(Thrown(() => ThrowWithParameters("s", 1)))!;

        result.Should().NotMatchRegex(@":0(
|$)");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowAtKnownLine(out int unused)
    {
        unused = 0;
        throw new KnownLine(ThisLine()); // ThisLine() must stay on the throw's own line
    }

    private static int ThisLine([CallerLineNumber] int line = 0) => line;

    private sealed class KnownLine(int line) : Exception("x") { public int Line { get; } = line; }

    private static Type ImpostorType()
    {
        var asm = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
            new System.Reflection.AssemblyName("ForeignDyn"), System.Reflection.Emit.AssemblyBuilderAccess.Run);
        var module = asm.DefineDynamicModule("ForeignDyn");
        var type = module.DefineType("Reqnroll.IdeSupport.Impostor", System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Abstract | System.Reflection.TypeAttributes.Sealed);
        var method = type.DefineMethod("Run", System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.Static, typeof(void), new[] { typeof(Action) });
        var il = method.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, typeof(Action).GetMethod("Invoke")!);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);
        return type.CreateType()!;
    }

    private interface IHandler<T> { void Handle(); }

    private sealed class ExplicitImpl : IHandler<UserSecretType>
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        void IHandler<UserSecretType>.Handle() => throw new InvalidOperationException("explicit");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LocalInLambda()
    {
        Action a = () =>
        {
            void L() => throw new InvalidOperationException("local");
            L();
        };
        a();
    }

    private static IEnumerable<int> Iterator()
    {
        yield return 1;
        throw new InvalidOperationException("iterator");
    }

    private static int ThrowingProperty => throw new InvalidOperationException("property");

    private static class ThrowingStaticCtor
    {
        static ThrowingStaticCtor() => throw new InvalidOperationException("cctor");
        public static void Touch() { }
    }

    private sealed class OuterGeneric<T>
    {
        public sealed class Inner
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Go() => throw new InvalidOperationException("nested");
        }
    }

    private sealed class AcmeCustomerException(string message, Exception inner) : Exception(message, inner);

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

internal static class SanitizedFrameExtensions
{
    /// <summary>"Ns.Type.Method:123" -> "Ns.Type.Method"; line numbers shift whenever the test file is edited.</summary>
    public static string WithoutLine(this string frame) => Regex.Replace(frame, @":\d+$", string.Empty);
}
