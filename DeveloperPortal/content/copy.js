// The copy button beside a new key. The button stays hidden without a
// script or a clipboard, and the key is still there to select.
for (const button of document.querySelectorAll('[data-copy]')) {
	const target = document.getElementById(button.dataset.copy);
	if (!target || !navigator.clipboard) continue;
	button.hidden = false;
	button.addEventListener('click', async () => {
		try {
			await navigator.clipboard.writeText(target.textContent.trim());
			button.textContent = 'Copied';
		} catch {
			button.textContent = 'Select the key and copy it';
		}
	});
}
