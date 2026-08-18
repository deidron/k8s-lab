# Rulesets

Repository rules kept here as the source of truth. **GitHub does not read this
directory** — a ruleset only takes effect once imported, through
Settings → Rules → Rulesets → Import a ruleset, or with the API:

```bash
gh api -X POST repos/deidron/k8s-lab/rulesets --input .github/rulesets/main-protection.json
```

Editing a file afterwards changes nothing until it is imported again; an
existing ruleset is updated with `-X PUT .../rulesets/<id>`.

## main-protection.json

The one worth applying now. `main` cannot be deleted or force-pushed, history
stays linear, and both CI checks must pass — with
`strict_required_status_checks_policy`, which is what makes a stale branch
unmergeable. Without it a pull request keeps a green check from before the base
moved, and merges on evidence that no longer holds.

The check names must match the job names in
[../workflows/ci.yml](../workflows/ci.yml). A context that never reports blocks
every merge permanently, so they are worth re-reading after any workflow rename.
`Build and publish image` is deliberately not required: it is skipped on pull
requests and would never report there.

There is no `pull_request` rule, so direct pushes to `main` still work. Add one
to require pull requests for everything; `required_approving_review_count: 0`
keeps that usable for a single maintainer, and
`"allowed_merge_methods": ["squash"]` pairs with the linear-history rule.

The admin bypass is a deliberate escape hatch. Rules with no bypass actor and a
required check that cannot report lock the repository for everyone, including
the owner.

## branch-naming.json

Blocks creating branches outside the usual prefixes. Correct as written —
`dependabot/**` is excluded, so dependency updates keep working.

Little to do here while the only branch author is Dependabot, whose branches are
already allowed. It becomes useful with more than one contributor.

## tag-protection.json

Stops `v*` tags being deleted, moved or overwritten. Nothing to act on yet: the
project has no release tags, since images are identified by commit SHA.

Worth applying as soon as releases start being tagged. A tag that can be moved
is a quiet way to make a published version point at different code.
