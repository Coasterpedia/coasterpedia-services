// Copy buttons, on every page. Without a script or a clipboard there are no
// buttons, and the text is still there to select.
//
// - The new key: a hidden button already in the page, named by data-copy.
// - A command: a <pre data-command>, or a ```sh block in a guide, which
//   marked renders as <pre><code class="language-sh">. Its button is added
//   here, beside the block.
if (navigator.clipboard) {
	for (const button of document.querySelectorAll('[data-copy]')) {
		const target = document.getElementById(button.dataset.copy);
		if (!target) continue;
		button.hidden = false;
		wire(button, target);
	}

	const commands = new Set(document.querySelectorAll('pre[data-command]'));
	for (const code of document.querySelectorAll('pre > code.language-sh')) commands.add(code.parentElement);
	for (const pre of commands) {
		const wrapper = document.createElement('div');
		wrapper.className = 'command';
		pre.replaceWith(wrapper);
		const button = document.createElement('button');
		button.type = 'button';
		button.className = 'btn';
		button.textContent = 'Copy';
		wrapper.append(pre, button);
		wire(button, pre, 'Copy');
	}
}

// If the browser refuses the clipboard, the text is selected for the reader
// to copy. `again` is the label to go back to, for a button used repeatedly.
function wire(button, target, again) {
	let timer;
	button.addEventListener('click', async () => {
		clearTimeout(timer);
		try {
			await navigator.clipboard.writeText(target.textContent.trim());
			button.textContent = 'Copied';
		} catch {
			getSelection().selectAllChildren(target);
			button.textContent = 'Selected';
		}
		if (again) timer = setTimeout(() => { button.textContent = again; }, 3000);
	});
}
