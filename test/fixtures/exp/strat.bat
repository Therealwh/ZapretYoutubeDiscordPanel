start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=80,443,12 ^
--filter-udp=443 --hostlist="%LISTS%list-general.txt" --new
