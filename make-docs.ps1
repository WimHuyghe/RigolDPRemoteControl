# Copies the application help (source/RigolDPRemoteControl/Help) to docs/ for GitHub:
#   docs/index.html  – the styled help, served by GitHub Pages
#   docs/HELP.md     – the same content, readable directly on github.com
# Run after changing the help:  powershell -ExecutionPolicy Bypass -File make-docs.ps1
$root = Split-Path $MyInvocation.MyCommand.Path
$help = Join-Path $root 'source\RigolDPRemoteControl\Help'
$docs = Join-Path $root 'docs'
New-Item -ItemType Directory -Force "$docs\img" | Out-Null

Copy-Item "$help\Help.html" "$docs\index.html" -Force
Copy-Item "$help\rigol_logo.png" "$docs\rigol_logo.png" -Force
Copy-Item "$help\img\*" "$docs\img\" -Force

# GitHub renders HTML inside Markdown (without the style sheet): take the <main> part of the help,
# without indentation and blank lines (those would turn parts into code blocks), as one HTML block.
$html = [IO.File]::ReadAllText("$help\Help.html")
$a = $html.IndexOf('<main>') + 6
$b = $html.IndexOf('</main>')
$main = $html.Substring($a, $b - $a)
$main = [regex]::Replace($main, '<span class="bn">(\d)</span><div><b>', '<div><b>$1. ')      # benefit cards
$main = [regex]::Replace($main, '<!--.*?-->', '', 'Singleline')
$lines = $main -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_.Length -gt 0 }
$md = @(
  '<!-- Generated from source/RigolDPRemoteControl/Help/Help.html by make-docs.ps1 – edit the help there. -->'
  '<!-- The styled version of this help is docs/index.html (GitHub Pages). -->'
  ''
) + $lines
[IO.File]::WriteAllText("$docs\HELP.md", ($md -join "`n") + "`n", (New-Object Text.UTF8Encoding $false))
"docs updated: index.html, HELP.md, img/"
