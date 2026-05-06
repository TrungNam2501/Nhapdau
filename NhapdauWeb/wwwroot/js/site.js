// Intersection Observer for scroll animations
document.addEventListener('DOMContentLoaded', function () {
    var animatedEls = document.querySelectorAll('.animate-fade-in-up, .animate-fade-in');
    if ('IntersectionObserver' in window) {
        var observer = new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) {
                    entry.target.style.visibility = 'visible';
                    observer.unobserve(entry.target);
                }
            });
        }, { threshold: 0.1 });
        animatedEls.forEach(function (el) { observer.observe(el); });
    }
});
