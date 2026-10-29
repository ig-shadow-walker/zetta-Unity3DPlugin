# Release notes for maintainers

Unity ignores folders whose name ends in `~`, so nothing here is imported into a project.

## Update notifications

Installed copies of the plugin check once a day for a newer release, and show a banner in the Zetta 3D
window with **What's new**, **Update** and **✕**. Update installs the newest release through the Package
Manager.

**A normal release needs nothing extra.** The latest version is the `version` in `package.json` on `main`,
and every release already bumps it. "What's new" opens the GitHub release for the `vX.Y.Z` tag.

**A required update** is one that older copies must install to keep working, for example after an API
change they can't handle. In the release commit, edit `update.json`:

```json
{
  "minimum": "0.8.0",
  "message": "This version works with the new pipeline runs."
}
```

- `minimum` is the oldest version that still works. Anything older shows a red banner that only hides until
  Unity restarts. Use `null` when nothing is required.
- `message` is optional (up to 300 characters). It is shown after the banner text, on both kinds of update.
  Use `null` for none.

Copies installed before 0.7.2 don't have the checker and never see the banner.

## Where copies read it

The plugin asks the Zetta 3D server, which reads `package.json` and this file from `main`. If the server
can't be reached, the plugin reads the same two files from GitHub itself. A change reaches copies within
about 10 minutes of being pushed to `main`, the next time each copy checks. In an emergency the server can
override both without a commit.
