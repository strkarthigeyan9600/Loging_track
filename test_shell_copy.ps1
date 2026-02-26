$shell = New-Object -ComObject Shell.Application
$src = (Get-Item "TestSrc").FullName
$dest = (Get-Item "TestDest").FullName
$folder = $shell.NameSpace($dest)
$item = $shell.NameSpace($src).ParseName("test.txt")
$folder.CopyHere($item)
Start-Sleep -Seconds 2