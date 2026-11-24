## Summary

<!-- A concise description of what this PR does and why. -->

## Type of Change

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Performance improvement
- [ ] Refactor / code cleanup
- [ ] Documentation update
- [ ] Breaking change

## Related Issue(s)

Closes #

## Changes Made

<!-- List the files/areas changed and what was done. -->

## Testing Done

- [ ] Builds successfully (`dotnet build --configuration Release` with 0 errors)
- [ ] Tested in Quantower on a live symbol
- [ ] Tested sound alerts (buy + sell)
- [ ] Tested toggle activation — no spurious alerts
- [ ] Tested chart scroll/pan — no false alerts during viewport changes
- [ ] Tested parameter change / resubscribe

## Screenshots / Recordings

<!-- Add screenshots of the indicator on a live chart if applicable. -->

## Checklist

- [ ] Code follows the existing style and partial-class structure
- [ ] No new heap allocations introduced inside `OnPaintChart`
- [ ] No ThreadPool thread blocking in alert / audio code
- [ ] `CHANGELOG.md` updated
- [ ] XML doc comments added/updated for changed public/internal members
