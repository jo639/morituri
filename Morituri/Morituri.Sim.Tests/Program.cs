#if NUNIT_SHIM
using System.Reflection;
using NUnit.Framework;

int passed = 0, failed = 0;
var failures = new List<string>();
string? filter = args.Length > 0 ? args[0] : null;   // 테스트 이름 부분 일치 필터(선택)

var fixtures = Assembly.GetExecutingAssembly().GetTypes()
    .Where(t => t.GetCustomAttribute<TestFixtureAttribute>() is not null)
    .OrderBy(t => t.Name);

foreach (var fixture in fixtures)
{
    Console.WriteLine($"\n■ {fixture.Name}");
    var instance = Activator.CreateInstance(fixture);
    var tests = fixture.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.GetCustomAttribute<TestAttribute>() is not null)
        .Where(m => filter == null || m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

    foreach (var test in tests)
    {
        try
        {
            test.Invoke(instance, null);
            Console.WriteLine($"  ✓ {test.Name}");
            passed++;
        }
        catch (TargetInvocationException e) when (e.InnerException is AssertionException ae)
        {
            Console.WriteLine($"  ✗ {test.Name}");
            failures.Add($"{fixture.Name}.{test.Name}: {ae.Message}");
            failed++;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  ✗ {test.Name} (예외)");
            failures.Add($"{fixture.Name}.{test.Name}: {e.InnerException ?? e}");
            failed++;
        }
    }
}

Console.WriteLine($"\n────────────────────────────");
Console.WriteLine($"통과 {passed} / 실패 {failed} / 전체 {passed + failed}");
foreach (var f in failures) Console.WriteLine($"\n[FAIL] {f}");
return failed == 0 ? 0 : 1;
#endif
