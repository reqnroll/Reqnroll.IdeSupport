import { ScenarioTestTargetDto } from './scenarioTestTarget';

/**
 * Builds a `dotnet test --filter` expression covering every distinct generated method among
 * `targets` — an OR'd list of exact `FullyQualifiedName=...` terms. Row-tests targets
 * (design doc §3: multiple targets sharing one `MethodName`, `IsParameterized = true`) collapse to
 * a single term, since running that one method already runs every row (design doc §5, "Row-tests
 * mode (default) — free"). Individual-methods targets (distinct `MethodName` per row) each need
 * their own term — design doc §5 confirms this is the case VS Code can do trivially, since it owns
 * its own `dotnet test --filter` invocation (unlike VS/Rider, where multi-target invocation is
 * unconfirmed/unchecked).
 */
export function buildTestFilter(targets: readonly ScenarioTestTargetDto[]): string {
  const fqns = new Set<string>();
  for (const target of targets) {
    fqns.add(`${target.declaringTypeFullName}.${target.methodName}`);
  }
  return [...fqns].map((fqn) => `FullyQualifiedName=${fqn}`).join('|');
}
