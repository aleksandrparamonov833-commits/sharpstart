$ErrorActionPreference = 'Stop'
$baseUrl = if ($env:SHARPSTART_URL) { $env:SHARPSTART_URL.TrimEnd('/') } else { 'http://localhost:5147' }

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) { throw "Проверка не пройдена: $message" }
}

$health = Invoke-RestMethod "$baseUrl/api/health"
Assert-True ($health.status -eq 'ok') 'API health'

$lessons = Invoke-RestMethod "$baseUrl/api/lessons"
$lessonCount = $lessons.Count
Assert-True ($lessonCount -eq 31) "в каталоге 31 урок (получено $lessonCount)"
$start = Invoke-RestMethod "$baseUrl/api/lessons/start"
Assert-True ($start.checks.Count -eq 3) 'три мини-проверки первого урока'
Assert-True ($start.exercises.Count -eq 3) 'три задания первого урока'

$quizBody = @{ checkId = 'check-1'; option = 'Console.WriteLine' } | ConvertTo-Json
$quiz = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/lessons/start/quiz" -ContentType 'application/json' -Body $quizBody
Assert-True ([bool]$quiz.correct) 'верный ответ проверяется API'

$runBody = @{ code = 'Console.WriteLine("SharpStart smoke test");'; input = '' } | ConvertTo-Json
$run = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/code/run" -ContentType 'application/json' -Body $runBody
Assert-True ([bool]$run.success) 'валидный C# запускается в песочнице'
Assert-True ($run.output.Trim() -eq 'SharpStart smoke test') 'редактор возвращает stdout'

$invalidBody = @{ code = 'Console.WriteLine("missing semicolon")'; input = '' } | ConvertTo-Json
$invalid = Invoke-RestMethod -Method Post -Uri "$baseUrl/api/code/run" -ContentType 'application/json' -Body $invalidBody
Assert-True (-not [bool]$invalid.success) 'ошибка компиляции не считается успешным запуском'
Assert-True (-not [string]::IsNullOrWhiteSpace($invalid.stderr)) 'компилятор возвращает диагностику'

$docs = Invoke-WebRequest "$baseUrl/docs.html"
$docCount = [regex]::Matches($docs.Content, '<details class="doc-card"').Count
Assert-True ($docCount -eq 19) "в справочнике 19 тем (получено $docCount)"

Write-Host 'Smoke test passed: health, course, quiz, C# execution, compiler diagnostics and reference.' -ForegroundColor Green
