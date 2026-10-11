#!/usr/bin/env bash
# html_escape unit tests (H-ESC): every HTML-significant byte must be escaped
# under bash 5.2 patsub_replacement rules, where a bare & in the replacement
# expands to the matched text.
# shellcheck disable=SC1091
source "$(dirname "$0")/lib.sh"

COMMON="$(repo_root)/scripts/lib/common.sh"
# shellcheck disable=SC1091
source "$COMMON"

# Exact H-ESC payload from the stress report.
assert_eq "escape-h-esc-payload" \
    '&lt;script&gt;alert(1)&lt;/script&gt; &amp; &quot;q&quot; it&#39;s' \
    "$(html_escape '<script>alert(1)</script> & "q" it'"'"'s')"

# Single-quote must not survive (H-5 img-src breakout vector).
assert_eq "escape-single-quote" 'x&#39; onerror=&#39;alert(1)' \
    "$(html_escape "x' onerror='alert(1)")"

# Style-breakout vector must not survive (H-4).
assert_eq "escape-style-breakout" '&lt;/style&gt;&lt;script&gt;alert(1)&lt;/script&gt;' \
    "$(html_escape '</style><script>alert(1)</script>')"

# &amp; round-trip: a pre-escaped entity gets its & escaped (no unescape).
assert_eq "escape-amp-entity" '&amp;amp;' "$(html_escape '&amp;')"

# Plain text is untouched.
assert_eq "escape-plain" 'Hello 123 -_.' "$(html_escape 'Hello 123 -_.')"
