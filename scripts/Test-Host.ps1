[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$OutputRoot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Net.Http
New-Item -ItemType Directory -Path $OutputRoot -ErrorAction Stop | Out-Null
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
[IO.File]::WriteAllText((Join-Path $OutputRoot 'host.json'),'{}')
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
$process=Start-Process -FilePath $Executable -ArgumentList @('--config',('"'+(Join-Path $OutputRoot 'host.json')+'"'),'--data',('"'+(Join-Path $OutputRoot 'state')+'"'),'--port',"$port") -PassThru -WindowStyle Hidden
$handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false;$handler.AllowAutoRedirect=$false
$client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(2)
$base='http://127.0.0.1:'+$port
$results=@()
try {
 $ready=$false
 for($attempt=0;$attempt -lt 40;$attempt++){
  if($process.HasExited){throw 'Host exited before health check'}
  try{$reply=$client.GetAsync($base+'/health').GetAwaiter().GetResult();if($reply.IsSuccessStatusCode){$ready=$true;break}}catch{}
  Start-Sleep -Milliseconds 200
 }
 if(-not $ready){throw 'Host did not become healthy'}
 $health=$reply.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json
 if($health.schema -ne 'cino.native-host.health.v1' -or $health.version -ne '0.4.0-network.2' -or $health.production_ready){throw 'Incorrect health/version'}
 $results+='actual host health and version'
 $status=($client.GetAsync($base+'/api/status').GetAwaiter().GetResult()).Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json
 if($status.registered -or $status.link_state -ne 'not_configured'){throw 'Unexpected network registration'}
 $results+='unconfigured host does not register'
 foreach($case in @(@('POST','/bridge/poll',401),@('POST','/execute',404),@('POST','/api/status',405))){
  $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($case[0]),$base+$case[1])
  $request.Content=[Net.Http.StringContent]::new('{}')
  $response=$client.SendAsync($request).GetAwaiter().GetResult()
  if([int]$response.StatusCode -ne $case[2]){throw ('Unexpected access result: '+$case[1])}
  $results+=('access boundary '+$case[1]);$response.Dispose();$request.Dispose()
 }
 $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,$base+'/health')
 $request.Headers.Host='untrusted.invalid'
 $response=$client.SendAsync($request).GetAwaiter().GetResult()
 if([int]$response.StatusCode -ne 400){throw 'Foreign Host header accepted'}
 $results+='foreign Host header rejected';$response.Dispose();$request.Dispose()
 [ordered]@{schema='cino.opensource.host-tests.v1';passed=$results.Count;results=$results;target_changed=$false;network_e2e_verified=$false}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $OutputRoot 'report.json') -Encoding UTF8
 Write-Output ("PASS host process checks: "+$results.Count)
}finally{
 $client.Dispose()
 if(-not $process.HasExited){$process.Kill();$process.WaitForExit()}
 $process.Dispose()
}

