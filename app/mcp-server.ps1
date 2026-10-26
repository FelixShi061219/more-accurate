param()

$ErrorActionPreference = 'Stop'
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$stateDirectory = Join-Path $env:LOCALAPPDATA 'MoreAccurateIndicator'
$statePath = Join-Path $stateDirectory 'state.json'

function Send-ProtocolMessage($message) {
    [Console]::Out.WriteLine(($message | ConvertTo-Json -Depth 20 -Compress))
    [Console]::Out.Flush()
}

while (($line = [Console]::In.ReadLine()) -ne $null) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }

    try { $request = $line | ConvertFrom-Json -ErrorAction Stop }
    catch { continue }

    $id = $request.id
    switch ($request.method) {
        'initialize' {
            Send-ProtocolMessage @{ jsonrpc = '2.0'; id = $id; result = @{ protocolVersion = '2024-11-05'; capabilities = @{ tools = @{} }; serverInfo = @{ name = 'more-accurate-indicator'; version = '1.0.0' } } }
        }
        'notifications/initialized' { }
        'ping' { Send-ProtocolMessage @{ jsonrpc = '2.0'; id = $id; result = @{} } }
        'tools/list' {
            $tool = @{
                name = 'update_confidence'
                description = 'Update the floating confidence indicator after every response. Report both requirement-understanding and answer-reliability scores with concise evidence.'
                inputSchema = @{
                    type = 'object'
                    properties = @{
                        understanding = @{ type = 'integer'; minimum = 0; maximum = 100; description = 'How completely the request, scope, constraints, and acceptance criteria are understood.' }
                        reliability = @{ type = 'integer'; minimum = 0; maximum = 100; description = 'Evidence-based estimated reliability of the current answer.' }
                        activeMode = @{ type = 'string'; enum = @('understanding', 'reliability'); description = 'Which score the bar should display.' }
                        note = @{ type = 'string'; maxLength = 240; description = 'Short reason for a change or remaining uncertainty.' }
                    }
                    required = @('understanding', 'reliability', 'activeMode')
                }
            }
            Send-ProtocolMessage @{ jsonrpc = '2.0'; id = $id; result = @{ tools = @($tool) } }
        }
        'tools/call' {
            if ($request.params.name -ne 'update_confidence') {
                Send-ProtocolMessage @{ jsonrpc = '2.0'; id = $id; error = @{ code = -32602; message = 'Unknown tool.' } }
                continue
            }

            try {
                $input = $request.params.arguments
                foreach ($scoreName in @('understanding', 'reliability')) {
                    $score = [int]$input.$scoreName
                    if ($score -lt 0 -or $score -gt 100) { throw "$scoreName must be between 0 and 100." }
                }
                if ($input.activeMode -notin @('understanding', 'reliability')) { throw 'activeMode is invalid.' }

                [System.IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
                $payload = @{
                    understanding = [int]$input.understanding
                    reliability = [int]$input.reliability
                    activeMode = [string]$input.activeMode
                    note = [string]$input.note
                    updatedAt = [DateTime]::UtcNow.ToString('o')
                } | ConvertTo-Json -Compress
                $temporaryPath = "$statePath.$PID.tmp"
                [System.IO.File]::WriteAllText($temporaryPath, $payload, [System.Text.UTF8Encoding]::new($false))
                Move-Item -LiteralPath $temporaryPath -Destination $statePath -Force

                $content = "Updated: understanding $($input.understanding)%, reliability $($input.reliability)%."
                Send-ProtocolMessage @{ jsonrpc = '2.0'; id = $id; result = @{ content = @(@{ type = 'text'; text = $content }) } }
            }
            catch {
                Send-ProtocolMessage @{ jsonrpc = '2.0'; id = $id; result = @{ isError = $true; content = @(@{ type = 'text'; text = $_.Exception.Message }) } }
            }
        }
        default {
            if ($null -ne $id) { Send-ProtocolMessage @{ jsonrpc = '2.0'; id = $id; error = @{ code = -32601; message = 'Method not found.' } } }
        }
    }
}
