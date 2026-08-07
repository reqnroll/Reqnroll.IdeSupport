/**
 * Mirrors `ScenarioTestTargetDto`/`ResolveTestTargetsResponse` in
 * `src/LSP/Reqnroll.IdeSupport.LSP.Server/Features/TestTargets/ResolveTestTargetsResponse.cs` —
 * the response shape of the `reqnroll/resolveTestTargets` request (design doc §3/§4).
 */
export interface ScenarioTestTargetDto {
  readonly declaringTypeFullName: string;
  readonly methodName: string;
  readonly isParameterized: boolean;
  readonly rowArguments?: Record<string, string> | null;
  readonly rowIndex?: number | null;
}

export interface ResolveTestTargetsResponse {
  readonly targets: ScenarioTestTargetDto[];
}
