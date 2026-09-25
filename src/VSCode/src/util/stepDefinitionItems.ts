import { showWarn } from '../logging/appNotify';

/**
 * One step-definition binding as the server reports it in `reqnroll/findUnusedStepDefinitions`
 * and `reqnroll/goToStepDefinition` (the two share this item shape — `StepDefinitionItem.cs`).
 */
export interface StepDefinitionItem {
  projectName?: string;
  className?: string;
  methodName?: string;
  bindingExpression?: string;
  /** `Given`/`When`/`Then`; absent when unknown or from a server predating issue #757. */
  stepDefinitionType?: string;
  /** Absent when the binding's source file does not exist on this machine — see `isResolved`. */
  sourceFile?: string;
  sourceLine: number;
  sourceChar: number;
  /**
   * Whether `sourceFile` names a file that exists here. False when the assembly was built
   * elsewhere (a container, a CI agent, another machine, an external binding package) and the
   * source path it recorded could not be mapped onto this workspace. Older servers omit the
   * field; callers treat it as `?? true` so those behave exactly as before.
   */
  isResolved?: boolean;
  /** The path the compiled assembly records, when it differs from `sourceFile`. */
  recordedSourceFile?: string;
}

/** `ClassName.MethodName`, or whichever part is known. */
export function formatMethodName(item: StepDefinitionItem): string {
  return [item.className, item.methodName].filter(Boolean).join('.');
}

/**
 * The binding attribute as it would appear on the method (issue #757) — `[Given("the sum is {int}")]`,
 * or `[Given]` for a method-name-style binding with no expression. With no known keyword the
 * expression is shown quoted on its own; with neither, `undefined`. Matches the Visual Studio and
 * Rider step-definition lists.
 */
export function formatBindingAttribute(item: StepDefinitionItem): string | undefined {
  // Truthiness, not `??`: an empty string is as good as absent for display.
  const keyword = item.stepDefinitionType;
  const expression = item.bindingExpression;
  if (keyword && expression) return `[${keyword}("${expression}")]`;
  if (keyword) return `[${keyword}]`;
  if (expression) return `"${expression}"`;
  return undefined;
}

/**
 * Explains, rather than silently doing nothing, why a binding whose source isn't on this machine
 * can't be opened (issue #540). The server nulls `sourceFile` precisely so callers reach this.
 */
export function warnSourceNotOnThisMachine(item: StepDefinitionItem): void {
  const recorded = item.recordedSourceFile;
  void showWarn(
    recorded
      ? `Reqnroll: this step definition's source isn't on this machine. The compiled assembly records it at "${recorded}". Rebuild the project locally to navigate to it.`
      : "Reqnroll: this step definition's source isn't on this machine. Rebuild the project locally to navigate to it.",
  );
}
