# Dynamic compliance wake — emit sentinel after delay. Interval chosen by agent.
param([int]$Seconds = 900)
Start-Sleep -Seconds $Seconds
Write-Output 'AGENT_LOOP_WAKE_compliance {"prompt":"Check compliance with .agent/DOCA_DESK_BRIEF.md: audit working tree vs brief requirements for current milestone and completed work; list concrete bugs/gaps; continue fixing toward M1-M7 until none remain. Do not mark goal complete until evidence proves full brief compliance."}'
