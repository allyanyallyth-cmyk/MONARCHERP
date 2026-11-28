param(
    [string]$Root = (Join-Path $PSScriptRoot '..')
)
$ErrorActionPreference = 'Stop'
$rootPath = (Resolve-Path $Root).Path
$sourcePath = Join-Path $rootPath 'src\Monarch.Desktop\MainWindow.xaml.cs'
if (-not (Test-Path -LiteralPath $sourcePath)) {
    throw "Desktop source file not found: $sourcePath"
}
$content = [System.IO.File]::ReadAllText($sourcePath)

$firstPattern = '(?s)"سيتم إنشاء نسخة احتياطية والتحقق منها ثم ترقية المخطط التجاري\.\s*أغلق جميع نسخ MONARCH الأخرى التي تستخدم قاعدة البيانات\. احتفظ بالنسخة الاحتياطية ولا تحذفها قبل التحقق من النظام\.\s*هل توافق على تنفيذ الترقية الآن؟"'
$firstReplacement = '"سيتم إنشاء نسخة احتياطية والتحقق منها ثم ترقية المخطط التجاري.\n\nأغلق جميع نسخ MONARCH الأخرى التي تستخدم قاعدة البيانات. احتفظ بالنسخة الاحتياطية ولا تحذفها قبل التحقق من النظام.\n\nهل توافق على تنفيذ الترقية الآن؟"'
$secondPattern = '(?s)"اكتملت الترقية\. يوصى بإغلاق MONARCH وإعادة تشغيله قبل متابعة العمل\.\s*النسخة الاحتياطية محفوظة في:\s*" \+ result\.BackupPath,'
$secondReplacement = '"اكتملت الترقية. يوصى بإغلاق MONARCH وإعادة تشغيله قبل متابعة العمل.\n\nالنسخة الاحتياطية محفوظة في:\n" + result.BackupPath,'

$firstCount = [regex]::Matches($content, $firstPattern).Count
$secondCount = [regex]::Matches($content, $secondPattern).Count
if ($firstCount -eq 1) { $content = [regex]::Replace($content, $firstPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $firstReplacement }, 1) }
elseif ($firstCount -ne 0) { throw "Expected one legacy confirmation string, found $firstCount." }
if ($secondCount -eq 1) { $content = [regex]::Replace($content, $secondPattern, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $secondReplacement }, 1) }
elseif ($secondCount -ne 0) { throw "Expected one legacy completion string, found $secondCount." }

if ($content -match '(?s)"سيتم إنشاء نسخة احتياطية والتحقق منها ثم ترقية المخطط التجاري\.\s*أغلق جميع نسخ' -or
    $content -match '(?s)"اكتملت الترقية\. يوصى بإغلاق MONARCH وإعادة تشغيله قبل متابعة العمل\.\s*النسخة الاحتياطية') {
    throw 'A multiline regular C# string remains unescaped.'
}
[System.IO.File]::WriteAllText($sourcePath, $content, [System.Text.UTF8Encoding]::new($false))
Write-Host 'PASS: repaired legacy multiline Arabic C# message literals without changing message text.'

# Convert generic Enum.Parse<TEnum>(...) calls in the restored legacy source to
# the .NET Framework-compatible Enum.Parse(typeof(TEnum), ...) overload.
# The balancing-group expression preserves nested method calls in arguments.
$enumParsePattern = [regex]::new('Enum\.Parse<(?<enumType>[^<>]+)>\((?<arguments>(?:[^()]|\((?<nested>)|\)(?<-nested>))*)(?(nested)(?!))\)')
$enumParseReplacements = 0
$enumParseEvaluator = [System.Text.RegularExpressions.MatchEvaluator]{
    param($match)
    $enumType = $match.Groups['enumType'].Value
    $arguments = $match.Groups['arguments'].Value
    "Enum.Parse(typeof($enumType), $arguments)"
}
Get-ChildItem -LiteralPath (Join-Path $rootPath 'src') -Filter '*.cs' -File -Recurse | ForEach-Object {
    $fileContent = [System.IO.File]::ReadAllText($_.FullName)
    $updatedContent = $enumParsePattern.Replace($fileContent, $enumParseEvaluator)
    if ($updatedContent -ne $fileContent) {
        $enumParseReplacements += [regex]::Matches($fileContent, 'Enum\.Parse<').Count
        [System.IO.File]::WriteAllText($_.FullName, $updatedContent, [System.Text.UTF8Encoding]::new($false))
    }
}
$remainingGenericEnumParse = Get-ChildItem -LiteralPath (Join-Path $rootPath 'src') -Filter '*.cs' -File -Recurse |
    Select-String -Pattern 'Enum\.Parse\s*<' -List
if ($remainingGenericEnumParse) {
    throw "Unsupported generic Enum.Parse syntax remains in: $($remainingGenericEnumParse.Path -join ', ')"
}
Write-Host "PASS: converted $enumParseReplacements generic Enum.Parse call(s) to .NET Framework-compatible overloads."



$desktopNavigationPatch = Join-Path $rootPath 'scripts\apply-desktop-navigation.py'
if (-not (Test-Path -LiteralPath $desktopNavigationPatch)) {
    throw "Desktop navigation patch was not found: $desktopNavigationPatch"
}
python $desktopNavigationPatch $rootPath
if ($LASTEXITCODE -ne 0) {
    throw "Desktop navigation and tax RBAC patch failed."
}

$sqliteStressPatch = Join-Path $rootPath 'scripts\patch-sqlite-concurrency-test.py'
if (-not (Test-Path -LiteralPath $sqliteStressPatch)) {
    throw "SQLite concurrency regression patch was not found: $sqliteStressPatch"
}
python $sqliteStressPatch $rootPath
if ($LASTEXITCODE -ne 0) {
    throw "SQLite concurrency regression test patch failed."
}
