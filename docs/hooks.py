"""Resolve included Markdown links relative to their repository source."""

import posixpath
import re
from pathlib import Path
from urllib.parse import urlsplit

from markdown import Markdown

SOURCES = {
    "index.md": "README.md",
    "server.md": "server/README.md",
    "contributing.md": "CONTRIBUTING.md",
    "changelog.md": "CHANGELOG.md",
    "security-policy.md": "SECURITY.md",
}


def on_page_markdown(markdown, page, config, files):
    root = Path(config.config_file_path).parent
    source = SOURCES.get(page.file.src_uri, f"docs/{page.file.src_uri}")
    # Expand snippets before MkDocs validates links, retaining one source of content.
    processor = Markdown(
        extensions=["pymdownx.snippets"],
        extension_configs={
            "pymdownx.snippets": {"base_path": [str(root)], "check_paths": True}
        },
    )
    markdown = "\n".join(processor.preprocessors["snippet"].run(markdown.splitlines()))
    pages = {value: key for key, value in SOURCES.items()}
    if page.file.src_uri in SOURCES:
        page.edit_url = f"{config.repo_url}/edit/main/{source}"

    def resolve(target):
        if target.startswith(config.site_url):
            relative = target[len(config.site_url) :]
            path, separator, anchor = relative.partition("#")
            candidate = f"{path.rstrip('/')}.md" if path else "index.md"
            if files.get_file_from_path(candidate):
                return candidate + (separator + anchor if separator else "")
        url = urlsplit(target)
        if url.scheme or url.netloc or not url.path:
            return target
        path = posixpath.normpath(posixpath.join(posixpath.dirname(source), url.path))
        suffix = (f"?{url.query}" if url.query else "") + (
            f"#{url.fragment}" if url.fragment else ""
        )
        if path in pages:
            return pages[path] + suffix
        if path.startswith("docs/"):
            return (
                posixpath.relpath(path[5:], posixpath.dirname(page.file.src_uri))
                + suffix
            )
        if (root / path).is_file():
            return f"{config.repo_url}/blob/main/{path}{suffix}"
        return target

    markdown = re.sub(
        r"(\]\()([^\s)]+)(\))", lambda m: m[1] + resolve(m[2]) + m[3], markdown
    )
    markdown = re.sub(
        r'(\b(?:src|srcset)=")([^"]+)(")',
        lambda m: m[1] + resolve(m[2]) + m[3],
        markdown,
    )
    return markdown
