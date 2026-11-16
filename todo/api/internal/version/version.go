// Package version holds the build version. The release build stamps both
// values with -ldflags -X; a local build keeps the defaults.
package version

// Version is the release tag, or "dev" for a build that was not stamped.
var Version = "dev"

// Commit is the git commit the binary was built from, or "unknown".
var Commit = "unknown"
