// Monaco's bundled PowerShell language supplies highlighting, but no cmdlet completions.
const commands = {
    'Write-Host': 'Display a message in the host console.',
    'Write-Output': 'Send objects to the output pipeline.',
    'Write-Error': 'Write an error record.',
    'Write-Warning': 'Write a warning message.',
    'Write-Verbose': 'Write a verbose message.',
    'Write-Debug': 'Write a debug message.',
    'Write-Information': 'Write an information message.',
    'Read-Host': 'Read input from the console.',
    'Get-ChildItem': 'List items in a directory or provider path.',
    'Get-Item': 'Get an item at a path.',
    'Get-ItemProperty': 'Read properties of a file or registry key.',
    'Set-ItemProperty': 'Set a property of a file or registry key.',
    'New-Item': 'Create a file, directory, or provider item.',
    'Remove-Item': 'Remove an item.',
    'Copy-Item': 'Copy an item.',
    'Move-Item': 'Move an item.',
    'Rename-Item': 'Rename an item.',
    'Test-Path': 'Check whether a path exists.',
    'Join-Path': 'Combine a parent path and child path.',
    'Split-Path': 'Return part of a path.',
    'Get-Content': 'Read the contents of a file.',
    'Set-Content': 'Replace the contents of a file.',
    'Add-Content': 'Append content to a file.',
    'Out-File': 'Send output to a file.',
    'Get-Process': 'Get running processes.',
    'Start-Process': 'Start a process.',
    'Stop-Process': 'Stop a process.',
    'Get-Service': 'Get services.',
    'Start-Service': 'Start a service.',
    'Stop-Service': 'Stop a service.',
    'Restart-Service': 'Restart a service.',
    'Set-Service': 'Change service settings.',
    'Start-Sleep': 'Pause script execution.',
    'Get-Command': 'Discover available commands.',
    'Get-Help': 'Get help for a command.',
    'Get-Date': 'Get the current date and time.',
    'Get-Location': 'Get the current working location.',
    'Set-Location': 'Change the working location.',
    'Get-CimInstance': 'Query CIM instances.',
    'Invoke-WebRequest': 'Send a web request.',
    'Invoke-RestMethod': 'Send a request to a REST endpoint.',
    'Import-Module': 'Load a PowerShell module.',
    'ForEach-Object': 'Process each object in a pipeline.',
    'Where-Object': 'Filter objects in a pipeline.',
    'Select-Object': 'Select objects or properties.',
    'Sort-Object': 'Sort objects by property values.',
    'Measure-Object': 'Measure properties of objects.',
    'ConvertTo-Json': 'Convert objects to JSON.',
    'ConvertFrom-Json': 'Convert JSON to objects.',
    'Import-Csv': 'Read objects from a CSV file.',
    'Export-Csv': 'Write objects to a CSV file.'
};

export function registerPowerShellCompletions(monaco) {
    return monaco.languages.registerCompletionItemProvider('powershell', {
        triggerCharacters: ['-'],
        provideCompletionItems(model, position) {
            // Include the hyphen and any suffix so accepting a suggestion replaces the full cmdlet.
            const line = model.getLineContent(position.lineNumber);
            const prefix = line.slice(0, position.column - 1).match(/[\w-]*$/)[0];
            const suffix = line.slice(position.column - 1).match(/^[\w-]*/)[0];
            const startColumn = position.column - prefix.length;
            if (startColumn > 1 && /[$@\w-]/.test(line[startColumn - 2])) return { suggestions: [] };
            const range = new monaco.Range(position.lineNumber, startColumn,
                position.lineNumber, position.column + suffix.length);
            return {
                suggestions: Object.entries(commands)
                    .filter(([label]) => label.toLowerCase().startsWith(prefix.toLowerCase()))
                    .map(([label, documentation]) => ({
                        label, documentation, range,
                        kind: monaco.languages.CompletionItemKind.Function,
                        insertText: label,
                        detail: 'PowerShell cmdlet'
                    }))
            };
        }
    });
}
