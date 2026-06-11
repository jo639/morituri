#if NUNIT_SHIM
// ─────────────────────────────────────────────────────────────────────────────
// NUnit 호환 미니 심 (네트워크 제한 환경 전용).
// 실제 NUnit 패키지를 쓸 수 있는 환경에서는 이 파일과 Program.cs를 삭제하고
// csproj의 PackageReference 주석을 해제하면 테스트 코드는 무변경으로 동작한다.
// 구현 범위: 이 프로젝트의 테스트가 실제 사용하는 API만.
// ─────────────────────────────────────────────────────────────────────────────
namespace NUnit.Framework;

[AttributeUsage(AttributeTargets.Class)]
public sealed class TestFixtureAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

public interface IConstraint
{
    bool Matches(object? actual);
    string Describe();
}

public static class Assert
{
    public static void That(object? actual, IConstraint constraint, string? message = null)
    {
        if (!constraint.Matches(actual))
            throw new AssertionException(
                $"{message ?? "Assertion failed"}\n  기대: {constraint.Describe()}\n  실제: {actual}");
    }
}

public static class Is
{
    public static EqualConstraint EqualTo(object expected) => new(expected, negate: false);
    public static RangeConstraint InRange(double lo, double hi) => new(lo, hi);
    public static CompareConstraint GreaterThan(double v) => new(v, greater: true);
    public static CompareConstraint LessThan(double v) => new(v, greater: false);
    public static BoolConstraint True => new(true);
    public static BoolConstraint False => new(false);
    public static NotBuilder Not => new();

    public sealed class NotBuilder
    {
        public EqualConstraint EqualTo(object expected) => new(expected, negate: true);
    }
}

public sealed class EqualConstraint : IConstraint
{
    private readonly object _expected;
    private readonly bool _negate;
    private double? _tolerance;

    public EqualConstraint(object expected, bool negate) { _expected = expected; _negate = negate; }

    public EqualConstraint Within(double tolerance) { _tolerance = tolerance; return this; }

    public bool Matches(object? actual)
    {
        bool equal;
        if (_tolerance.HasValue && actual is not null && IsNumeric(actual) && IsNumeric(_expected))
            equal = Math.Abs(Convert.ToDouble(actual) - Convert.ToDouble(_expected)) <= _tolerance.Value;
        else
            equal = Equals(actual, _expected)
                 || (actual is not null && IsNumeric(actual) && IsNumeric(_expected)
                     && Convert.ToDouble(actual) == Convert.ToDouble(_expected));
        return _negate ? !equal : equal;
    }

    public string Describe()
        => $"{(_negate ? "≠ " : "")}{_expected}{(_tolerance.HasValue ? $" ±{_tolerance}" : "")}";

    private static bool IsNumeric(object o)
        => o is float or double or int or long or uint or ulong or short or byte or decimal;
}

public sealed class RangeConstraint(double lo, double hi) : IConstraint
{
    public bool Matches(object? actual)
        => actual is not null && Convert.ToDouble(actual) >= lo && Convert.ToDouble(actual) <= hi;
    public string Describe() => $"[{lo}, {hi}] 범위 내";
}

public sealed class CompareConstraint(double v, bool greater) : IConstraint
{
    public bool Matches(object? actual)
        => actual is not null && (greater ? Convert.ToDouble(actual) > v : Convert.ToDouble(actual) < v);
    public string Describe() => greater ? $"> {v}" : $"< {v}";
}

public sealed class BoolConstraint(bool expected) : IConstraint
{
    public bool Matches(object? actual) => actual is bool b && b == expected;
    public string Describe() => expected.ToString();
}
#endif
