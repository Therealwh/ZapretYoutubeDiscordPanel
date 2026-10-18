# Pinned zapret engine digests
#
# The release workflow downloads the "latest" upstream zip from
# Flowseal/zapret-discord-youtube and refuses to ship it unless its SHA-256
# matches a line in this file. To adopt a new engine version:
#   1. Check upstream's release digest (or hash the asset yourself).
#   2. Add one line below in the format:  <upstream tag> | <sha256 lowercase>
#   3. Old entries stay: older tags keep building reproducibly.
#
# GitHub exposes this digest as assets[].digest on the upstream release API;
# it is double-checked here so a tampered or swapped asset fails the build
# instead of reaching users' machines.

1.10.3 | 244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058
