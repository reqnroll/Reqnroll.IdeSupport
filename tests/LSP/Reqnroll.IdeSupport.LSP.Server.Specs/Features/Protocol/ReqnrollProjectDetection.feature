Feature: Reqnroll project detection
	The server tells the client, once, when the workspace contains a project that uses Reqnroll
	(reqnroll/reqnrollProjectDetected). Clients hold their non-error telemetry until then, so a
	solution without Reqnroll sends none.

Scenario: A project that does not use Reqnroll is not reported
	Given the LSP server is started
	When the project "Library.csproj" is announced in folder "Library"
	Then the client is not told that a Reqnroll project was detected

Scenario: The first project that references a Reqnroll package is reported once
	Given the LSP server is started
	When the project "Library.csproj" is announced in folder "Library"
	And the project is announced with output assembly "bin/Debug/Specs.dll" for "Specs.feature" referencing package "Reqnroll.xUnit"
	And the project "Other.csproj" is announced in folder "Other"
	Then the client is told once that a Reqnroll project was detected
