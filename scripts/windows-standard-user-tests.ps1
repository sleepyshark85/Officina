<#
.SYNOPSIS
Runs the Windows sandbox tests once as a standard user: not an administrator, at medium integrity, as `sof` runs for most
people. CI runs as an elevated administrator otherwise. This is the recipe the sandbox spike (S00a) used.

.DESCRIPTION
Creates a local standard user with a random password, copies the built sandbox tests to a folder only that user, the
administrators and the system can use, runs them there as that user, prints their output, checks that they ran at medium
integrity and not as an administrator, and deletes the user and the folder again, also when the tests fail. Exits with 1
when a test fails. It refuses to run outside GitHub Actions, as it creates a local user.

Run it on a Windows runner after the Release build, from the repository's root:

    pwsh scripts/windows-standard-user-tests.ps1

As a CI step of the Windows job, after "Test":

    - name: Windows sandbox tests as a standard user
      if: runner.os == 'Windows'
      shell: pwsh
      run: ./scripts/windows-standard-user-tests.ps1
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# It creates a local user, so it runs only on a CI runner, which is discarded afterwards.
if (-not $env:GITHUB_ACTIONS) {
    throw "This script creates a local user; it runs only on a GitHub Actions runner."
}

$built = Join-Path $PSScriptRoot "..\tests\Sleepyshark.Officina.Sandbox.Tests\bin\$Configuration\net10.0"
if (-not (Test-Path (Join-Path $built "Sleepyshark.Officina.Sandbox.Tests.dll"))) {
    throw "Build the solution in $Configuration first: $built has no sandbox tests."
}

# Under PowerShell 7 the local-accounts cmdlets may need Windows PowerShell's module; Windows PowerShell has them already.
if (-not (Get-Command New-LocalUser -ErrorAction SilentlyContinue)) {
    Import-Module Microsoft.PowerShell.LocalAccounts -UseWindowsPowerShell
}

$user = "sofstandard"
$root = "C:\sof-standard-user"
$password = ConvertTo-SecureString ("Sf!" + [guid]::NewGuid().ToString("N").Substring(0, 10)) -AsPlainText -Force
$created = $false
try {
    # New-LocalUser takes the password as a SecureString, so it is on no command line, and it throws when the user cannot be made.
    New-LocalUser -Name $user -Password $password -AccountNeverExpires -UserMayNotChangePassword | Out-Null
    $created = $true
    # New-LocalUser puts the account in no group; a standard user is in Users (S-1-5-32-545), which logging on needs.
    Add-LocalGroupMember -SID S-1-5-32-545 -Member $user
    New-Item -ItemType Directory -Force "$root\temp" | Out-Null
    Copy-Item -Recurse -Force $built "$root\tests"
    # Only that user, the administrators (S-1-5-32-544) and the system (S-1-5-18) may use the folder: what it would inherit from
    # C:\, where authenticated users may change subfolders, is removed.
    icacls $root /inheritance:r /grant:r "${user}:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F" /T | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls could not set the folder's permissions."
    }

    $log = "$root\tests.log"
    # The test project is an executable (xunit v3), so it runs with no restore and no access to the administrator's packages. Its
    # temporary folder is set, as the process may get the administrator's environment, whose temporary folder that user cannot use.
    # The space before each >> keeps cmd from reading the exit code's digit as a handle number.
    $dotnet = (Get-Command dotnet).Source
    $command = "set `"TEMP=$root\temp`" & set `"TMP=$root\temp`" & whoami /groups > `"$log`" 2>&1 & echo END OF GROUPS >> `"$log`" & " +
        "`"$dotnet`" `"$root\tests\Sleepyshark.Officina.Sandbox.Tests.dll`" -class Sleepyshark.Officina.Sandbox.Tests.WindowsSandboxTests >> `"$log`" 2>&1 " +
        "&& (echo EXIT 0 >> `"$log`") || (echo EXIT 1 >> `"$log`")"
    $credential = New-Object System.Management.Automation.PSCredential($user, $password)
    Start-Process -FilePath cmd.exe -ArgumentList "/c", $command -Credential $credential -Wait -WorkingDirectory $root -LoadUserProfile

    Get-Content $log
    # Only whoami's part of the log, so the tests' own output cannot match.
    $text = (Get-Content -Raw $log) -split "END OF GROUPS" | Select-Object -First 1
    if ($text -notmatch "Mandatory Label\\Medium Mandatory Level" -or $text -match "BUILTIN\\Administrators") {
        Write-Error "The tests did not run as a standard user at medium integrity."
        exit 1
    }

    if (-not (Select-String -Path $log -Pattern "^EXIT 0\s*$" -Quiet)) {
        Write-Error "The Windows sandbox tests failed as a standard user."
        exit 1
    }
}
finally {
    try {
        if ($created) {
            Remove-LocalUser -Name $user
        }
    }
    finally {
        Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
    }

    # The user's profile folder stays until the runner is discarded; it holds nothing but what the tests left in it.
}
